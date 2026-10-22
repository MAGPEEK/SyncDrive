using SyncDrive.Client.App.Ports;
using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Sync;

/// <summary>Ergebnis eines Rescans: was passiert ist und was nicht gelang.</summary>
public sealed record RescanReport(IReadOnlyList<PathOutcome> Outcomes, IReadOnlyList<(RelativePath Path, SyncError Error)> Failures);

/// <summary>
/// Eine Sync-Aufgabe zur Laufzeit. Zwei Wege führen zur <see cref="SyncEngine"/>:
/// <list type="bullet">
/// <item><see cref="RescanAsync"/> vergleicht einen ganzen Teilbaum (lokal, Basis, Server); Sicherheitsnetz nach Start, Watcher-Überlauf und
///   abgelaufenem Feed-Cursor.</item>
/// <item><see cref="PullChangesAsync"/> arbeitet den Änderungsfeed des Servers ab, optional als Long-Poll.</item>
/// </list>
/// </summary>
public sealed class SyncSession(
    SyncEngine engine, ILocalStore local, IRemoteApi remote, ISessionState state, Status.ISyncObserver? observer = null)
{
    public const int FeedPageSize = 500;
    private const int MaxAttemptsPerPath = 3;

    private readonly PathLocks _locks = new();

    /// <summary>Pause der Aufgabe: jede Bewertung und Verschiebung wartet hier, auch im Start-Rescan und beim Abarbeiten des Feeds.</summary>
    public SyncGate Gate { get; } = new();

    /// <summary>Vom Watcher gemeldete, noch nicht übertragene Verschiebungen; Bewertungen der betroffenen Pfade warten darauf.</summary>
    public PendingMoves Moves { get; } = new();

    /// <summary>
    /// Erstabgleich bzw. Neustart: neueste Revision merken, alles vergleichen, dann den Cursor auf die gemerkte Revision setzen. Was während
    /// des Rescans auf dem Server passiert, liegt hinter dem Cursor und kommt über den Feed noch einmal (idempotent, also harmlos).
    /// </summary>
    public async Task<RescanReport> StartAsync(bool startup, CancellationToken ct)
    {
        var latest = await remote.GetLatestRevisionAsync(ct);
        if (!latest.IsSuccess)
        {
            return new RescanReport([], [(RelativePath.Root, latest.Error!)]);
        }

        observer?.Connected();

        var report = await RescanAsync(RelativePath.Root, startup, ct);
        if (report.Failures.All(f => f.Error.Kind == SyncErrorKind.Permanent))
        {
            // Dauerhafte Fehler (z. B. eine nicht darstellbare Datei) werden durch Wiederholen nicht besser; sie dürfen den Feed nicht blockieren.
            await state.SetFeedCursorAsync(latest.Value, ct);
        }

        return report;
    }

    public async Task<RescanReport> RescanAsync(RelativePath root, bool startup, CancellationToken ct)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var localByKey = new Dictionary<string, LocalEntry>(StringComparer.Ordinal);
        var baseByKey = new Dictionary<string, SyncedNode>(StringComparer.Ordinal);
        await foreach (var item in local.ScanAsync(root, ct))
        {
            if (item is ScanItem.Found found)
            {
                paths.Add(found.Entry.Path.Value);
                localByKey[found.Entry.Path.Value.ToUpperInvariant()] = found.Entry;
            }
        }

        await foreach (var known in state.ListAsync(root, ct))
        {
            paths.Add(known.Path.Value);
            baseByKey[known.Path.Value.ToUpperInvariant()] = known;
        }

        await DetectLocalMovesAsync(localByKey, baseByKey, ct);

        var snapshot = await remote.GetSnapshotAsync(root, ct);
        if (!snapshot.IsSuccess)
        {
            return new RescanReport([], [(root, snapshot.Error!)]);
        }

        foreach (var entry in snapshot.Value!)
        {
            paths.Add(entry.Path);
        }

        var outcomes = new List<PathOutcome>();
        var failures = new List<(RelativePath, SyncError)>();
        var pending = new Queue<RelativePath>(paths.Select(RelativePath.Parse)); // sortiert: Eltern vor Kindern
        while (pending.TryDequeue(out var path))
        {
            var result = await EvaluateWithRetryAsync(path, startup, ct);
            if (!result.IsSuccess)
            {
                failures.Add((path, result.Error!));
                continue;
            }

            outcomes.Add(result.Value!);
            if (result.Value!.ConflictCopy is { } copy)
            {
                pending.Enqueue(copy);
            }
        }

        return new RescanReport(outcomes, failures);
    }

    /// <summary>
    /// Arbeitet den Feed ab dem gespeicherten Cursor ab. Der Cursor rückt nur über vollständig verarbeitete Seiten vor; schlägt ein Pfad fehl,
    /// bleibt er stehen und dieselben Änderungen kommen beim nächsten Mal wieder. Bei <c>cursor_expired</c> folgt ein vollständiger Abgleich.
    /// </summary>
    /// <param name="wait">Long-Poll-Dauer für die erste Abfrage; null = sofort antworten.</param>
    /// <returns>Anzahl der verarbeiteten Änderungen.</returns>
    public async Task<Result<int>> PullChangesAsync(TimeSpan? wait, CancellationToken ct)
    {
        var cursor = await state.GetFeedCursorAsync(ct);
        var processed = 0;
        while (true)
        {
            var page = await remote.GetChangesAsync(cursor, FeedPageSize, processed == 0 ? wait : null, ct);
            if (!page.IsSuccess)
            {
                return page.Error!.Code == ErrorCode.CursorExpired ? await ResyncAsync(ct) : Result.Fail<int>(page.Error);
            }

            foreach (var change in page.Value!.Changes)
            {
                var applied = await ApplyChangeAsync(change, ct);
                if (!applied.IsSuccess)
                {
                    return Result.Fail<int>(applied.Error!);
                }

                processed++;
            }

            cursor = page.Value.NextCursor;
            await state.SetFeedCursorAsync(cursor, ct);
            if (!page.Value.HasMore)
            {
                return Result.Ok(processed);
            }
        }
    }

    /// <summary>Räumt den lokalen Papierkorb auf (A14); Fehler dabei halten die Synchronisierung nicht auf.</summary>
    public async Task<int> CleanTrashAsync(TimeSpan maxAge, long maxBytes, CancellationToken ct)
    {
        try
        {
            return await local.CleanTrashAsync(maxAge, maxBytes, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0; // gesperrte Datei im Papierkorb: beim nächsten Mal
        }
    }

    /// <summary>Wiederholt bei vorübergehenden Fehlern, die durch erneutes Bewerten verschwinden (z. B. <c>revision_conflict</c>).</summary>
    /// <summary>
    /// Bewertung auf ein Watcher-Ereignis hin. Ist der Pfad lokal unverändert gegenüber der Basis, gibt es nichts hochzuladen; das Ereignis
    /// stammt dann meist von uns selbst (Download). Serveränderungen kommen geordnet über den Feed. Würde hier trotzdem gegen den aktuellen
    /// Serverstand bewertet, sähe eine Verschiebung, die der Feed noch nicht geliefert hat, wie eine Löschung aus.
    /// </summary>
    public async Task<Result<PathOutcome>> EvaluateLocalChangeAsync(RelativePath path, CancellationToken ct)
    {
        var known = await state.GetAsync(path, ct);
        var here = await local.StatAsync(path, ct);
        var unchanged = known is null
            ? here is null
            : here is not null && here.Kind == known.State.Kind
                && (here.Kind == NodeKind.Directory || (here.Size == known.State.Size && here.MTime == known.State.MTime));
        return unchanged
            ? Result.Ok(new PathOutcome(path, OutcomeKind.Unchanged))
            : await EvaluateWithRetryAsync(path, startup: false, ct);
    }

    public async Task<Result<PathOutcome>> EvaluateWithRetryAsync(RelativePath path, bool startup, CancellationToken ct)
    {
        await Moves.WaitClearAsync(path, ct);
        await Gate.WaitOpenAsync(ct);
        using var held = await _locks.AcquireAsync(path.Value, ct);
        observer?.PathStarted(path);
        Result<PathOutcome> result = Result.Fail<PathOutcome>(SyncErrorKind.Transient, "cancelled", "Abgebrochen.");
        try
        {
            for (var attempt = 1; attempt <= MaxAttemptsPerPath; attempt++)
            {
                result = await engine.EvaluateAsync(path, startup, ct);
                if (result.IsSuccess || result.Error!.Kind != SyncErrorKind.Transient || result.Error.Code == RemoteErrors.Network)
                {
                    break;
                }
            }

            return result;
        }
        finally
        {
            observer?.PathFinished(path, result);
        }
    }

    private async Task<Result<int>> ResyncAsync(CancellationToken ct)
    {
        var report = await StartAsync(startup: false, ct);
        return report.Failures.Count == 0
            ? Result.Ok(report.Outcomes.Count)
            : Result.Fail<int>(report.Failures[0].Error);
    }

    private async Task<Result<bool>> ApplyChangeAsync(ChangeEntry change, CancellationToken ct)
    {
        var path = RelativePath.Parse(change.Path);
        if (await state.GetAsync(path, ct) is { } known && known.NodeId == change.NodeId && known.RemoteRevision >= change.Revision)
        {
            // Echo einer eigenen Änderung: Die Basis kennt diesen Serverstand schon. Bewerten hieße nur, lokale Änderungen zu suchen – das ist
            // Sache von Watcher und Rescan. Hier wäre es sogar gefährlich: Ein gerade umbenannter Ordner, dessen Watcher-Ereignis noch
            // unterwegs ist, sähe gelöscht aus und würde auf dem Server gelöscht.
            return Result.Ok(true);
        }

        var notFollowedDirectory = false;
        if (change is { Op: ChangeOps.Move, OldPath: { } oldText })
        {
            var oldPath = RelativePath.Parse(oldText);
            bool followed;
            using (await AcquireBothAsync(oldPath, path, ct))
            {
                followed = await TryFollowRemoteMoveAsync(oldPath, path, change.NodeId, ct);
            }

            if (!followed)
            {
                notFollowedDirectory = change.Kind == NodeKinds.Directory;
                var old = await EvaluateWithRetryAsync(oldPath, startup: false, ct);
                if (!old.IsSuccess)
                {
                    return Result.Fail<bool>(old.Error!);
                }
            }
        }

        var result = await EvaluateWithRetryAsync(path, startup: false, ct);
        var newDirectory = result.Value is { Kind: OutcomeKind.Downloaded } && change.Kind == NodeKinds.Directory;
        if (result.IsSuccess && (notFollowedDirectory || newDirectory))
        {
            // Der Feed meldet bei Verschiebungen nur den Ordner, nicht seinen Inhalt: einer nicht mitverschobenen Verschiebung, einem von außerhalb
            // des Aufgabenordners hereingeschobenen Ordner. Der Inhalt muss dann einzeln kommen. Bei einem frisch angelegten Ordner ist der
            // Rescan billig; die Feed-Einträge seines Inhalts sind danach Echos und werden übersprungen.
            var report = await RescanAsync(path, startup: false, ct);
            return report.Failures.Count == 0 ? Result.Ok(true) : Result.Fail<bool>(report.Failures[0].Error);
        }

        return result.IsSuccess ? Result.Ok(true) : Result.Fail<bool>(result.Error!);
    }

    /// <summary>
    /// Server hat umbenannt/verschoben: Ist die lokale Fassung unverändert und das Ziel frei, wird lokal ebenfalls verschoben und die Basis
    /// umgeschrieben – keine Neuübertragung, auch nicht für große Ordner (Szenario 6 in 05-test-und-qualitaet.md).
    /// </summary>
    private async Task<bool> TryFollowRemoteMoveAsync(RelativePath from, RelativePath to, string nodeId, CancellationToken ct)
    {
        var known = await state.GetAsync(from, ct);
        var here = await local.StatAsync(from, ct);
        if (known is null || known.NodeId != nodeId || here is null || here.Kind != known.State.Kind)
        {
            return false;
        }

        var unchanged = here.Kind == NodeKind.Directory || (here.Size == known.State.Size && here.MTime == known.State.MTime);
        if (!unchanged || (await local.StatAsync(to, ct) is not null && !from.EqualsIgnoreCase(to)))
        {
            return false;
        }

        await local.MoveAsync(from, to, ct);
        await engine.RebaseAsync(from, to, ct);
        return true;
    }

    /// <summary>
    /// Lokale Umbenennung/Verschiebung (vom Watcher oder vom Rescan erkannt): beide Pfade sperren, als Server-Verschiebung übernehmen, danach
    /// den Inhalt am Ziel abgleichen. Gelingt die Verschiebung nicht, werden beide Pfade einzeln bewertet (löschen + neu, ohne Datenverlust).
    /// </summary>
    public async Task<Result<PathOutcome>> ApplyLocalMoveAsync(RelativePath from, RelativePath to, bool evaluateAfter, CancellationToken ct)
    {
        await Gate.WaitOpenAsync(ct);
        Result<PathOutcome> moved;
        using (await AcquireBothAsync(from, to, ct))
        {
            moved = await engine.TryMoveRemoteAsync(from, to, ct);
        }

        Moves.Complete(from, to);

        if (!moved.IsSuccess || !evaluateAfter)
        {
            return moved;
        }

        if (moved.Value!.Kind != OutcomeKind.Moved)
        {
            var old = await EvaluateWithRetryAsync(from, startup: false, ct);
            if (!old.IsSuccess)
            {
                return old;
            }
        }

        var target = await EvaluateWithRetryAsync(to, startup: false, ct);
        return moved.Value.Kind == OutcomeKind.Moved && target.IsSuccess ? moved : target;
    }

    /// <summary>
    /// Findet im Rescan Umbenennungen, die ohne Watcher passiert sind (offline, über Ordnergrenzen): fehlende Basis-Einträge gegen neue lokale
    /// Einträge, Ordner über gleiche Kindernamen, Dateien über Größe und Hash. Nur eindeutige Paare werden verschoben.
    /// </summary>
    private async Task DetectLocalMovesAsync(
        IReadOnlyDictionary<string, LocalEntry> localByKey, IReadOnlyDictionary<string, SyncedNode> baseByKey, CancellationToken ct)
    {
        static string Key(RelativePath p) => p.Value.ToUpperInvariant();
        bool TopMost(RelativePath p, Func<string, bool> parentPresent) => p.Parent.IsRoot || parentPresent(Key(p.Parent));

        var missing = baseByKey.Values
            .Where(b => !localByKey.ContainsKey(Key(b.Path)) && TopMost(b.Path, localByKey.ContainsKey))
            .ToList();
        var added = localByKey.Values
            .Where(l => !baseByKey.ContainsKey(Key(l.Path)) && TopMost(l.Path, baseByKey.ContainsKey))
            .ToList();
        if (missing.Count == 0 || added.Count == 0)
        {
            return;
        }

        var pairs = new List<(RelativePath From, RelativePath To)>();

        HashSet<string> BaseChildren(RelativePath dir) =>
            [.. baseByKey.Values.Where(b => b.Path.Parent.EqualsIgnoreCase(dir)).Select(b => b.Path.Name.ToUpperInvariant())];
        HashSet<string> LocalChildren(RelativePath dir) =>
            [.. localByKey.Values.Where(l => l.Path.Parent.EqualsIgnoreCase(dir)).Select(l => l.Path.Name.ToUpperInvariant())];

        foreach (var dir in missing.Where(m => m.State.Kind == NodeKind.Directory))
        {
            var names = BaseChildren(dir.Path);
            var candidates = added.Where(a => a.Kind == NodeKind.Directory && names.Count > 0 && LocalChildren(a.Path).SetEquals(names)).ToList();
            if (candidates.Count == 1)
            {
                pairs.Add((dir.Path, candidates[0].Path));
            }
        }

        var missingFiles = missing.Where(m => m.State is { Kind: NodeKind.File, Hash: not null }).ToList();
        foreach (var file in added.Where(a => a.Kind == NodeKind.File && missingFiles.Any(m => m.State.Size == a.Size)))
        {
            // Ein verschobener Platzhalter behält Größe und Datum; sein Inhalt ist nicht da und wird nicht geholt.
            var hash = file.IsCloudOnly ? (ContentHash?)null : await engine.HashLocalAsync(file.Path, ct);
            var sources = missingFiles
                .Where(m => m.State.Size == file.Size && (hash is null ? m.State.MTime == file.MTime : m.State.Hash == hash))
                .ToList();
            if (sources.Count == 1 && !pairs.Any(p => p.From.EqualsIgnoreCase(sources[0].Path)))
            {
                pairs.Add((sources[0].Path, file.Path));
            }
        }

        foreach (var (from, to) in pairs)
        {
            await ApplyLocalMoveAsync(from, to, evaluateAfter: false, ct);
        }
    }

    private async Task<IDisposable> AcquireBothAsync(RelativePath a, RelativePath b, CancellationToken ct)
    {
        var (first, second) = string.CompareOrdinal(a.Value.ToUpperInvariant(), b.Value.ToUpperInvariant()) <= 0 ? (a, b) : (b, a);
        var firstLease = await _locks.AcquireAsync(first.Value, ct);
        if (first.EqualsIgnoreCase(second))
        {
            return firstLease;
        }

        try
        {
            return new Leases(firstLease, await _locks.AcquireAsync(second.Value, ct));
        }
        catch
        {
            firstLease.Dispose();
            throw;
        }
    }

    private sealed class Leases(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            second.Dispose();
            first.Dispose();
        }
    }
}
