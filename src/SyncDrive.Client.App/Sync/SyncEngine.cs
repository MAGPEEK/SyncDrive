using System.Security.Cryptography;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Contracts;
using SyncDrive.Core.Chunking;
using SyncDrive.Core.Filtering;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Sync;

/// <summary>Was eine Sync-Aufgabe festlegt: Gerät (für Konfliktnamen), Richtung, Einstellungen, Filter, Blockgrößen.</summary>
/// <param name="AdvancedConsistencyCheck">„Erweiterte Konsistenzprüfung“: immer hashen statt Größe+mtime zu vertrauen (genauer, langsamer).</param>
/// <param name="ForceHashExtensions">Endungen, die immer gehasht werden, weil sich Inhalt ohne neue mtime ändern kann.</param>
/// <param name="OnDemand">Serverdateien kommen als Platzhalter ohne Inhalt an; der Inhalt folgt beim Öffnen (braucht <see cref="IPlaceholders"/>).</param>
public sealed record SyncTaskConfig(
    string DeviceName,
    SyncDirection Direction,
    ClientSettings Settings,
    FilterEngine Filter,
    ChunkerOptions Chunking,
    bool AdvancedConsistencyCheck = false,
    IReadOnlyList<string>? ForceHashExtensions = null,
    bool KeepLocallyDeletedOnServer = false,
    bool OnDemand = false)
{
    public static IReadOnlyList<string> DefaultForceHashExtensions { get; } = ["tc"];

    /// <summary>Konfiguration der Engine aus einer gespeicherten Aufgabe und den globalen Einstellungen.</summary>
    public static SyncTaskConfig From(Tasks.SyncTaskDefinition task, ClientSettings settings, string deviceName, ChunkerOptions? chunking = null) =>
        new(deviceName, task.Direction, settings, DefaultFilters.Create(task.Filters), chunking ?? new ChunkerOptions(),
            task.AdvancedConsistencyCheck, KeepLocallyDeletedOnServer: task.Direction == SyncDirection.UploadOnly && task.KeepLocallyDeletedOnServer,
            OnDemand: task.OnDemand);

    public MergeOptions MergeOptions(bool startupRescan)
    {
        var options = Settings.ToMergeOptions(Direction, startupRescan);
        return KeepLocallyDeletedOnServer ? options with { IgnoreLocalDelete = true } : options;
    }

    public bool MustHash(RelativePath path)
    {
        if (AdvancedConsistencyCheck)
        {
            return true;
        }

        var name = path.Name;
        var dot = name.LastIndexOf('.');
        return dot > 0 && (ForceHashExtensions ?? DefaultForceHashExtensions)
            .Any(e => string.Equals(e.TrimStart('.'), name[(dot + 1)..], StringComparison.OrdinalIgnoreCase));
    }
}

public enum OutcomeKind
{
    Unchanged,
    AdoptedBase,
    Uploaded,
    Downloaded,
    DeletedRemote,
    DeletedLocal,
    ConflictResolved,
    Skipped,

    /// <summary>Lokal umbenannt/verschoben und als Server-Verschiebung übernommen (keine Neuübertragung).</summary>
    Moved,
}

/// <param name="ConflictCopy">Neu entstandene Konfliktkopie; der Aufrufer bewertet sie als eigenen Pfad.</param>
public sealed record PathOutcome(
    RelativePath Path,
    OutcomeKind Kind,
    FilterReason? SkipReason = null,
    RelativePath? ConflictCopy = null,
    bool IsDirectory = false);

/// <summary>
/// Bewertet einen Pfad (Basis, lokal, remote), entscheidet über die <see cref="MergeEngine"/> und führt die Aktion aus.
/// Jeder Aufruf ist idempotent: Er liest den aktuellen Zustand neu, statt einem Ereignis zu vertrauen (ADR-2).
/// Erwartbare Fehler kommen als <see cref="Result{T}"/>; bei <c>revision_conflict</c> genügt es, den Pfad erneut zu bewerten.
/// Platzhalter (<see cref="LocalEntry.IsCloudOnly"/>) liest die Engine nie: Ihr Stand ist der der Basis, und sie gewinnen nie gegen den Server.
/// </summary>
/// <param name="placeholders">Nur für On-Demand-Aufgaben: legt Platzhalter statt vollständiger Dateien an.</param>
public sealed class SyncEngine(
    ILocalStore local,
    IRemoteApi remote,
    ISessionState state,
    SyncTaskConfig config,
    IActivityLog activity,
    TimeProvider clock,
    IPlaceholders? placeholders = null)
{
    private const int CopyBufferSize = 81920;

    public async Task<Result<PathOutcome>> EvaluateAsync(RelativePath path, bool startupRescan, CancellationToken ct)
    {
        try
        {
            return await EvaluateCoreAsync(path, startupRescan, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gesperrt, kurz weg, gerade verschoben: beim nächsten Durchgang wieder versuchen.
            return Result.Fail<PathOutcome>(SyncErrorKind.Transient, "local_io", e.Message);
        }
    }

    private async Task<Result<PathOutcome>> EvaluateCoreAsync(RelativePath path, bool startupRescan, CancellationToken ct)
    {
        var baseNode = await state.GetAsync(path, ct);
        var localEntry = await local.StatAsync(path, ct);
        var remoteResult = await remote.GetNodeAsync(path, ct);
        if (!remoteResult.IsSuccess)
        {
            return Result.Fail<PathOutcome>(remoteResult.Error!);
        }

        var remoteNode = remoteResult.Value;
        var (localState, chunked) = await ReadLocalAsync(path, localEntry, baseNode, ct);
        var remoteState = remoteNode?.State;

        if ((localState ?? remoteState) is { } probe && config.Filter.Evaluate(path, probe) is { Allowed: false } rejected)
        {
            return await SkipAsync(path, baseNode, localState is not null, probe.Kind, rejected.Reason!.Value, ct);
        }

        var options = config.MergeOptions(startupRescan);
        var action = MergeEngine.Decide(new PathTriple(path, baseNode?.State, localState, remoteState), options);
        if (action is SyncAction.NoOp && options.IgnoreLocalDelete && !config.KeepLocallyDeletedOnServer
            && localState is null && baseNode is not null && remoteState is not null)
        {
            // „Lokal gelöschte Dateien werden vom Server wieder geholt“ (Standard beim Neustart):
            // die Engine blendet die Löschung aus, der Client stellt die Datei aktiv wieder her.
            action = new SyncAction.Download();
        }

        if (localEntry is { IsCloudOnly: true } && action is SyncAction.Upload or SyncAction.Conflict)
        {
            // Ein Platzhalter hat keinen eigenen Inhalt und kann nie gewinnen: Serverfassung übernehmen, ohne sie Platzhalter entfernen.
            action = remoteState is { Kind: NodeKind.File } ? new SyncAction.Download() : new SyncAction.LocalDelete();
        }

        var work = new Work(path, baseNode, localEntry, localState, chunked, remoteNode);
        return action switch
        {
            SyncAction.NoOp => await RefreshBaseAsync(work, ct),
            SyncAction.AdoptBase => await AdoptBaseAsync(work, ct),
            SyncAction.Upload => await UploadAsync(work, ct),
            SyncAction.Download => await DownloadAsync(work, ct),
            SyncAction.RemoteDelete => await DeleteRemoteAsync(work, ct),
            SyncAction.LocalDelete => await DeleteLocalAsync(work, ct),
            SyncAction.Conflict conflict => await ResolveConflictAsync(work, conflict, ct),
            _ => throw new InvalidOperationException($"Unbekannte Aktion {action}"),
        };
    }

    /// <summary>Schwache Prüfung: Größe und mtime wie in der Basis ⇒ Hash der Basis übernehmen, sonst lesen und zerlegen.</summary>
    private async Task<(NodeState? State, ChunkedFile? Chunked)> ReadLocalAsync(
        RelativePath path, LocalEntry? entry, SyncedNode? baseNode, CancellationToken ct)
    {
        if (entry is null)
        {
            return (null, null);
        }

        if (entry.Kind == NodeKind.Directory)
        {
            return (NodeState.Directory(entry.MTime), null);
        }

        if (entry.IsCloudOnly)
        {
            // Nicht lesen (das würde den Inhalt holen). Ohne passende Basis bleibt der Hash offen; dann gilt Größe und Datum.
            var hash = baseNode?.State is { Kind: NodeKind.File, Hash: { } baseHash } b && b.Size == entry.Size ? baseHash : (ContentHash?)null;
            return (NodeState.File(entry.Size, entry.MTime, hash), null);
        }

        if (!config.MustHash(path)
            && baseNode?.State is { Kind: NodeKind.File, Hash: { } knownHash } known && known.Size == entry.Size && known.MTime == entry.MTime)
        {
            return (NodeState.File(entry.Size, entry.MTime, knownHash), null);
        }

        var chunked = await ChunkAsync(path, ct);
        return (NodeState.File(chunked.Size, entry.MTime, chunked.FileHash), chunked);
    }

    /// <summary>Inhaltshash einer lokalen Datei (für die Erkennung verschobener Dateien).</summary>
    public async Task<ContentHash> HashLocalAsync(RelativePath path, CancellationToken ct) => (await ChunkAsync(path, ct)).FileHash;

    private async Task<ChunkedFile> ChunkAsync(RelativePath path, CancellationToken ct)
    {
        await using var stream = await local.OpenReadAsync(path, ct);
        return await FastCdcChunker.ChunkAsync(stream, config.Chunking, ct);
    }

    private async Task<Result<PathOutcome>> SkipAsync(
        RelativePath path, SyncedNode? baseNode, bool isLocal, NodeKind kind, FilterReason reason, CancellationToken ct)
    {
        if (baseNode is not null)
        {
            await state.RemoveSubtreeAsync(path, ct); // ausgeschlossen: nur aus der Basis entfernen, nichts löschen
        }

        await LogAsync(isLocal ? ActivityAction.Upload : ActivityAction.Download, path, kind, ActivityResult.Unsynced, reason.ToString(), ct);
        return Result.Ok(new PathOutcome(path, OutcomeKind.Skipped, SkipReason: reason));
    }

    /// <summary>Unverändert. Hat sich nur die mtime bewegt (Inhalt gleich), wird die Basis nachgezogen, damit die schwache Prüfung wieder greift.</summary>
    private async Task<Result<PathOutcome>> RefreshBaseAsync(Work w, CancellationToken ct)
    {
        if (w.Base is { } known && w.LocalState is { Kind: NodeKind.File } current && current.MTime != known.State.MTime)
        {
            await state.SaveAsync(known with { State = current, SyncedAt = clock.GetUtcNow() }, ct);
        }

        return Done(w.Path, OutcomeKind.Unchanged);
    }

    private async Task<Result<PathOutcome>> AdoptBaseAsync(Work w, CancellationToken ct)
    {
        if (w.LocalState is null || w.Remote?.State is null)
        {
            await state.RemoveSubtreeAsync(w.Path, ct); // beide Seiten haben gelöscht
        }
        else
        {
            await SaveBaseAsync(w.Path, w.Remote.NodeId, w.LocalState, w.Remote.Revision, ct);
        }

        return Done(w.Path, OutcomeKind.AdoptedBase);
    }

    private async Task<Result<PathOutcome>> UploadAsync(Work w, CancellationToken ct)
    {
        var localState = w.LocalState!;
        var baseRevision = w.Remote?.Revision ?? 0;

        if (w.Remote?.State is { } remoteState && remoteState.Kind != localState.Kind)
        {
            var removed = await CommitAsync(new CommitRequest(CommitOps.Delete, w.Path.Value, baseRevision), ct);
            if (!removed.IsSuccess)
            {
                return Result.Fail<PathOutcome>(removed.Error!);
            }

            baseRevision = removed.Value!.Revision;
        }

        if (localState.Kind == NodeKind.Directory)
        {
            var created = await CommitAsync(new CommitRequest(CommitOps.Mkdir, w.Path.Value, baseRevision, MTime: w.LocalEntry!.MTime), ct);
            if (!created.IsSuccess)
            {
                return Result.Fail<PathOutcome>(created.Error!);
            }

            await SaveBaseAsync(w.Path, created.Value!.NodeId, localState, created.Value.Revision, ct);
            await LogAsync(ActivityAction.Upload, w.Path, NodeKind.Directory, ActivityResult.Done, null, ct);
            return Result.Ok(new PathOutcome(w.Path, OutcomeKind.Uploaded, IsDirectory: true));
        }

        var chunked = w.Chunked ?? await ChunkAsync(w.Path, ct);
        var sent = await SendMissingBlocksAsync(w.Path, chunked, ct);
        if (!sent.IsSuccess)
        {
            return Result.Fail<PathOutcome>(sent.Error!);
        }

        var request = new CommitRequest(
            CommitOps.Put, w.Path.Value, baseRevision,
            Blocks: [.. chunked.Chunks.Select(c => c.Hash.Value)],
            Size: chunked.Size,
            MTime: w.LocalEntry!.MTime,
            FileHash: chunked.FileHash.Value);
        var committed = await CommitAsync(request, ct);
        if (!committed.IsSuccess)
        {
            return Result.Fail<PathOutcome>(committed.Error!);
        }

        var uploaded = NodeState.File(chunked.Size, w.LocalEntry.MTime, chunked.FileHash);
        await SaveBaseAsync(w.Path, committed.Value!.NodeId, uploaded, committed.Value.Revision, ct);
        await LogAsync(ActivityAction.Upload, w.Path, NodeKind.File, ActivityResult.Done, null, ct);
        return Done(w.Path, OutcomeKind.Uploaded);
    }

    /// <summary>Sendet nur die Blöcke, die der Server noch nicht hat (Delta, Deduplizierung, Fortsetzen nach Abbruch).</summary>
    private async Task<Result<bool>> SendMissingBlocksAsync(RelativePath path, ChunkedFile chunked, CancellationToken ct)
    {
        var distinct = chunked.Chunks.Select(c => c.Hash).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return Result.Ok(true);
        }

        var missing = await remote.MissingBlocksAsync(distinct, ct);
        if (!missing.IsSuccess)
        {
            return Result.Fail<bool>(missing.Error!);
        }

        foreach (var hash in missing.Value!)
        {
            var chunk = chunked.Chunks.First(c => c.Hash == hash);
            var bytes = await ReadRangeAsync(path, chunk.Offset, chunk.Length, ct);
            if (ContentHash.FromBytes(SHA256.HashData(bytes)) != hash)
            {
                return Result.Fail<bool>(SyncErrorKind.Transient, "local_changed", "Datei hat sich während der Übertragung geändert.");
            }

            var uploaded = await remote.UploadBlockAsync(hash, new MemoryStream(bytes, writable: false), ct);
            if (!uploaded.IsSuccess)
            {
                return uploaded;
            }
        }

        return Result.Ok(true);
    }

    private async Task<byte[]> ReadRangeAsync(RelativePath path, long offset, int length, CancellationToken ct)
    {
        await using var stream = await local.OpenReadAsync(path, ct);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private async Task<Result<PathOutcome>> DownloadAsync(Work w, CancellationToken ct)
    {
        var remoteNode = w.Remote!;
        var remoteState = remoteNode.State!;
        if (w.LocalEntry is { } existing && existing.Kind != remoteState.Kind)
        {
            await local.MoveToTrashAsync(w.Path, ct); // Typwechsel: die lokale Fassung bleibt im Papierkorb erhalten
        }

        if (remoteState.Kind == NodeKind.Directory)
        {
            await local.CreateDirectoryAsync(w.Path, remoteState.MTime, ct);
            await SaveBaseAsync(w.Path, remoteNode.NodeId, NodeState.Directory(remoteState.MTime), remoteNode.Revision, ct);
            await LogAsync(ActivityAction.Download, w.Path, NodeKind.Directory, ActivityResult.Done, null, ct);
            return Done(w.Path, OutcomeKind.Downloaded);
        }

        if (config.OnDemand && placeholders is not null && remoteState.Hash is { } hash && w.LocalEntry is null or { IsCloudOnly: true })
        {
            // On-Demand: nur Name, Größe und Datum; der Inhalt kommt beim ersten Öffnen (Hydrator). Lokale Inhalte werden nie ersetzt.
            await placeholders.CreateAsync(w.Path, remoteState.Size, remoteState.MTime, hash, ct);
            var placed = await local.StatAsync(w.Path, ct);
            await SaveBaseAsync(w.Path, remoteNode.NodeId, NodeState.File(remoteState.Size, placed?.MTime ?? remoteState.MTime, hash), remoteNode.Revision, ct);
            await LogAsync(ActivityAction.Download, w.Path, NodeKind.File, ActivityResult.Done, null, ct);
            return Done(w.Path, OutcomeKind.Downloaded);
        }

        var fetched = await FetchToAsync(w.Path, remoteNode, ct);
        if (!fetched.IsSuccess)
        {
            return Result.Fail<PathOutcome>(fetched.Error!);
        }

        var written = await local.StatAsync(w.Path, ct);
        var baseState = NodeState.File(remoteState.Size, written?.MTime ?? remoteState.MTime, remoteState.Hash);
        await SaveBaseAsync(w.Path, remoteNode.NodeId, baseState, remoteNode.Revision, ct);
        await LogAsync(ActivityAction.Download, w.Path, NodeKind.File, ActivityResult.Done, null, ct);
        return Done(w.Path, OutcomeKind.Downloaded);
    }

    /// <summary>Lädt Block für Block in eine Staging-Datei, prüft jeden Block und den Gesamthash und macht die Datei erst dann sichtbar.</summary>
    private async Task<Result<bool>> FetchToAsync(RelativePath target, RemoteNode remoteNode, CancellationToken ct)
    {
        var expected = remoteNode.State!;
        await using var staged = await local.StageAsync(target, ct);
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var blockHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        foreach (var hash in remoteNode.Blocks)
        {
            var download = await remote.DownloadBlockAsync(hash, ct);
            if (!download.IsSuccess)
            {
                return Result.Fail<bool>(download.Error!);
            }

            await using (var block = download.Value!)
            {
                int read;
                while ((read = await block.ReadAsync(buffer, ct)) > 0)
                {
                    blockHash.AppendData(buffer, 0, read);
                    fileHash.AppendData(buffer, 0, read);
                    await staged.Content.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                }
            }

            if (ContentHash.FromBytes(blockHash.GetHashAndReset()) != hash)
            {
                return Result.Fail<bool>(SyncErrorKind.Transient, "block_corrupt", $"Block {hash} kam beschädigt an.");
            }
        }

        var actualHash = ContentHash.FromBytes(fileHash.GetHashAndReset());
        if (total != expected.Size || (expected.Hash is { } want && want != actualHash))
        {
            return Result.Fail<bool>(SyncErrorKind.Transient, "file_mismatch", "Heruntergeladener Inhalt passt nicht zur Serverfassung.");
        }

        await staged.CommitAsync(expected.MTime, ct);
        return Result.Ok(true);
    }

    private async Task<Result<PathOutcome>> DeleteRemoteAsync(Work w, CancellationToken ct)
    {
        if (w.Remote?.State is not null)
        {
            var deleted = await CommitAsync(new CommitRequest(CommitOps.Delete, w.Path.Value, w.Remote.Revision), ct);
            if (!deleted.IsSuccess)
            {
                return Result.Fail<PathOutcome>(deleted.Error!);
            }
        }

        await state.RemoveSubtreeAsync(w.Path, ct);
        await LogAsync(ActivityAction.Delete, w.Path, w.Base?.State.Kind ?? NodeKind.File, ActivityResult.Done, null, ct);
        return Done(w.Path, OutcomeKind.DeletedRemote);
    }

    private async Task<Result<PathOutcome>> DeleteLocalAsync(Work w, CancellationToken ct)
    {
        await local.MoveToTrashAsync(w.Path, ct); // nie endgültig löschen (A14)
        await state.RemoveSubtreeAsync(w.Path, ct);
        await LogAsync(ActivityAction.Delete, w.Path, w.LocalEntry?.Kind ?? NodeKind.File, ActivityResult.Done, null, ct);
        return Done(w.Path, OutcomeKind.DeletedLocal);
    }

    private async Task<Result<PathOutcome>> ResolveConflictAsync(Work w, SyncAction.Conflict conflict, CancellationToken ct)
    {
        var deletionInvolved = conflict.Kind is ConflictKind.ModifyDelete or ConflictKind.DeleteModify;
        if (deletionInvolved)
        {
            // Inhalt gegen Löschung: ohne Verlierer gewinnt der Inhalt; sonst setzt sich die Löschung durch, der Inhalt landet im Papierkorb.
            var result = (conflict.Winner, conflict.Loser) switch
            {
                (Side.Local, LoserHandling.None) => await UploadAsync(w, ct),
                (Side.Remote, LoserHandling.None) => await DownloadAsync(w, ct),
                (Side.Remote, _) => await DeleteLocalAsync(w, ct),
                _ => await DeleteRemoteAsync(w, ct),
            };
            return await AfterConflictAsync(w.Path, conflict, result, null, ct);
        }

        var copy = await FreeConflictPathAsync(w.Path, ct);
        if (conflict.Winner == Side.Remote)
        {
            // Lokal verliert: lokale Fassung als Konfliktkopie (oder Papierkorb), dann die Serverfassung holen.
            if (conflict.Loser == LoserHandling.CopyAside)
            {
                await local.MoveAsync(w.Path, copy, ct);
            }
            else
            {
                await local.MoveToTrashAsync(w.Path, ct);
            }

            var downloaded = await DownloadAsync(w with { LocalEntry = null, LocalState = null }, ct);
            return await AfterConflictAsync(w.Path, conflict, downloaded, conflict.Loser == LoserHandling.CopyAside ? copy : null, ct);
        }

        // Server verliert: seine Fassung wird auf dem Server zur Konfliktkopie umbenannt (bis es Versionen gibt, auch bei „Trash“),
        // danach ist der Pfad frei für die lokale Fassung.
        var moved = await CommitAsync(new CommitRequest(CommitOps.Move, w.Path.Value, w.Remote!.Revision, NewPath: copy.Value), ct);
        if (!moved.IsSuccess)
        {
            return Result.Fail<PathOutcome>(moved.Error!);
        }

        var uploaded = await UploadAsync(w with { Remote = null }, ct);
        return await AfterConflictAsync(w.Path, conflict, uploaded, copy, ct);
    }

    private async Task<Result<PathOutcome>> AfterConflictAsync(
        RelativePath path, SyncAction.Conflict conflict, Result<PathOutcome> step, RelativePath? copy, CancellationToken ct)
    {
        if (!step.IsSuccess)
        {
            return step;
        }

        await LogAsync(ActivityAction.Conflict, path, NodeKind.File, ActivityResult.Done, conflict.Kind.ToString(), ct);
        return Result.Ok(new PathOutcome(path, OutcomeKind.ConflictResolved, ConflictCopy: copy));
    }

    /// <summary>
    /// Übernimmt eine lokale Umbenennung/Verschiebung als Server-Verschiebung, wenn sie eindeutig ist: Quelle ist bekannt (Basis), lokal weg,
    /// auf dem Server noch derselbe Knoten; Ziel existiert lokal mit derselben Art und ist weder in der Basis noch auf dem Server belegt.
    /// Inhaltliche Änderungen am Ziel bewertet der Aufrufer danach wie gewohnt. Ohne Erfolg bleibt alles unverändert (dann: löschen + neu).
    /// </summary>
    public async Task<Result<PathOutcome>> TryMoveRemoteAsync(RelativePath from, RelativePath to, CancellationToken ct)
    {
        try
        {
            var known = await state.GetAsync(from, ct);
            var target = await local.StatAsync(to, ct);
            if (known is null || target is null || target.Kind != known.State.Kind
                || await local.StatAsync(from, ct) is not null || await state.GetAsync(to, ct) is not null)
            {
                return NotMoved(from);
            }

            var source = await remote.GetNodeAsync(from, ct);
            var occupied = await remote.GetNodeAsync(to, ct);
            if (!source.IsSuccess || !occupied.IsSuccess)
            {
                return Result.Fail<PathOutcome>((source.Error ?? occupied.Error)!);
            }

            if (source.Value is not { State: not null } node || node.NodeId != known.NodeId || occupied.Value?.State is not null)
            {
                return NotMoved(from);
            }

            var moved = await CommitAsync(new CommitRequest(CommitOps.Move, from.Value, node.Revision, NewPath: to.Value), ct);
            if (!moved.IsSuccess)
            {
                return moved.Error!.Kind == SyncErrorKind.Transient && moved.Error.Code != RemoteErrors.Network
                    ? NotMoved(from) // jemand war schneller: normal bewerten
                    : Result.Fail<PathOutcome>(moved.Error);
            }

            await RebaseAsync(from, to, ct);
            await LogAsync(ActivityAction.Move, to, known.State.Kind, ActivityResult.Done, from.Value, ct);
            return Result.Ok(new PathOutcome(to, OutcomeKind.Moved, IsDirectory: known.State.Kind == NodeKind.Directory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result.Fail<PathOutcome>(SyncErrorKind.Transient, "local_io", e.Message);
        }
    }

    /// <summary>Schreibt die Basis eines Teilbaums auf einen neuen Pfad um (nach einer Verschiebung, lokal oder auf dem Server).</summary>
    public async Task RebaseAsync(RelativePath from, RelativePath to, CancellationToken ct)
    {
        var nodes = new List<SyncedNode>();
        if (await state.GetAsync(from, ct) is { } root)
        {
            nodes.Add(root);
        }

        await foreach (var child in state.ListAsync(from, ct))
        {
            nodes.Add(child);
        }

        await state.RemoveSubtreeAsync(from, ct);
        foreach (var node in nodes)
        {
            var suffix = node.Path.Value[from.Value.Length..];
            await state.SaveAsync(node with { Path = RelativePath.Parse(to.Value + suffix) }, ct);
        }
    }

    private static Result<PathOutcome> NotMoved(RelativePath from) => Result.Ok(new PathOutcome(from, OutcomeKind.Unchanged));

    /// <summary>Konfliktname, der weder lokal noch auf dem Server belegt ist.</summary>
    private async Task<RelativePath> FreeConflictPathAsync(RelativePath path, CancellationToken ct)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var candidate = ConflictNamer.Create(path, config.DeviceName, clock.GetLocalNow(), p => taken.Contains(p.Value));
            var remoteNode = await remote.GetNodeAsync(candidate, ct);
            var usedRemotely = remoteNode is { IsSuccess: true, Value.State: not null };
            if (await local.StatAsync(candidate, ct) is null && !usedRemotely)
            {
                return candidate;
            }

            taken.Add(candidate.Value);
        }
    }

    private Task<Result<CommitResponse>> CommitAsync(CommitRequest request, CancellationToken ct) =>
        remote.CommitAsync(request, Guid.NewGuid().ToString("N"), ct);

    private Task SaveBaseAsync(RelativePath path, string nodeId, NodeState nodeState, long revision, CancellationToken ct) =>
        state.SaveAsync(new SyncedNode(path, nodeId, nodeState, revision, clock.GetUtcNow()), ct);

    private Task LogAsync(
        ActivityAction action, RelativePath path, NodeKind kind, ActivityResult result, string? reason, CancellationToken ct) =>
        activity.AppendAsync(new ActivityEntry(clock.GetUtcNow(), action, path, kind == NodeKind.Directory, result, reason), ct);

    private static Result<PathOutcome> Done(RelativePath path, OutcomeKind kind) => Result.Ok(new PathOutcome(path, kind));

    private sealed record Work(
        RelativePath Path,
        SyncedNode? Base,
        LocalEntry? LocalEntry,
        NodeState? LocalState,
        ChunkedFile? Chunked,
        RemoteNode? Remote);
}
