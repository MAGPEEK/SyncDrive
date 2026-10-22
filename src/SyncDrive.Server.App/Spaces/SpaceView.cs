using System.Diagnostics;
using SyncDrive.Contracts;
using SyncDrive.Core.Paths;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Server.App.Spaces;

/// <summary>
/// Ein Space, wie ihn ein Aufrufer sieht (ADR-12): ganz (Mitglied) oder ab einem geteilten Ordner (Einladung, öffentlicher Link). Pfade nach
/// außen sind relativ zu dieser Wurzel; der View rechnet sie um, verbietet Änderungen an der Wurzel selbst und filtert den Änderungsfeed auf
/// den sichtbaren Teilbaum. Ohne Wurzel reicht er alles unverändert an <see cref="SpaceService"/> durch.
/// </summary>
public sealed class SpaceView
{
    private readonly SpaceService _spaces;
    private readonly string _prefix;

    /// <param name="id">Space-Id nach außen: die des Space selbst oder die der Einladung (<c>sh_…</c>).</param>
    /// <param name="root">Pfad des sichtbaren Teilbaums im Space; <see cref="RelativePath.Root"/> = ganzer Space.</param>
    public SpaceView(SpaceService spaces, string id, string spaceId, RelativePath root, SpaceRole role)
    {
        _spaces = spaces;
        Id = id;
        SpaceId = spaceId;
        Root = root;
        Role = role;
        _prefix = root.Value + "/";
    }

    public string Id { get; }

    public string SpaceId { get; }

    public RelativePath Root { get; }

    public SpaceRole Role { get; }

    public bool IsScoped => !Root.IsRoot;

    /// <summary>Pfad im Space zu einem Pfad im View; null, wenn er ungültig ist oder (ohne <paramref name="allowRoot"/>) die Wurzel meint.</summary>
    public string? Inner(string? path, bool allowRoot = true)
    {
        if (!RelativePath.TryParse(path ?? string.Empty, out var parsed) || (parsed.IsRoot && !allowRoot))
        {
            return null;
        }

        return Root.IsRoot ? parsed.Value : parsed.IsRoot ? Root.Value : _prefix + parsed.Value;
    }

    /// <summary>Pfad im View zu einem Pfad im Space; null außerhalb des sichtbaren Teilbaums, leer für die Wurzel.</summary>
    public string? Outer(string? innerPath)
    {
        if (innerPath is null || Root.IsRoot)
        {
            return innerPath;
        }

        if (innerPath == Root.Value)
        {
            return string.Empty;
        }

        return innerPath.StartsWith(_prefix, StringComparison.Ordinal) ? innerPath[_prefix.Length..] : null;
    }

    public async Task<ServiceResult<CommitResponse>> CommitAsync(CommitRequest request, string? idempotencyKey, string? actorUserId, CancellationToken ct)
    {
        var path = Inner(request.Path, allowRoot: false);
        var newPath = request.NewPath is null ? null : Inner(request.NewPath, allowRoot: false);
        if (path is null || (request.NewPath is not null && newPath is null))
        {
            return InvalidPath<CommitResponse>();
        }

        return MapError(await _spaces.CommitAsync(SpaceId, request with { Path = path, NewPath = newPath }, idempotencyKey, actorUserId, ct));
    }

    public Task<ServiceResult<CursorResponse>> GetLatestRevisionAsync(CancellationToken ct) => _spaces.GetLatestRevisionAsync(SpaceId, ct);

    public async Task<ServiceResult<ChangesResponse>> GetChangesAsync(long after, int limit, CancellationToken ct)
    {
        var page = await _spaces.GetChangesAsync(SpaceId, after, limit, ct);
        return IsScoped && page.IsSuccess ? Filter(page.Value!) : page;
    }

    /// <summary>
    /// Long-Poll wie <see cref="SpaceService.WaitForChangesAsync"/>. Mit Wurzel wartet er weiter, solange nur Änderungen außerhalb des
    /// Teilbaums kommen, damit Clients nicht im Leerlauf kreisen; bei Zeitablauf trägt die leere Antwort den weitergerückten Cursor.
    /// </summary>
    public async Task<ServiceResult<ChangesResponse>> WaitForChangesAsync(long after, int limit, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsScoped)
        {
            return await _spaces.WaitForChangesAsync(SpaceId, after, limit, timeout, ct);
        }

        var watch = Stopwatch.StartNew();
        while (true)
        {
            var remaining = timeout - watch.Elapsed;
            var page = await _spaces.WaitForChangesAsync(SpaceId, after, limit, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct);
            if (!page.IsSuccess)
            {
                return page;
            }

            var filtered = Filter(page.Value!);
            if (!filtered.IsSuccess || filtered.Value!.Changes.Count > 0 || page.Value!.Changes.Count == 0 || remaining <= TimeSpan.Zero)
            {
                return filtered;
            }

            after = page.Value.NextCursor;
        }
    }

    public async Task<ServiceResult<NodeDetail>> GetNodeAsync(string path, CancellationToken ct)
    {
        if (Inner(path, allowRoot: false) is not { } inner)
        {
            return InvalidPath<NodeDetail>();
        }

        var result = await _spaces.GetNodeAsync(SpaceId, inner, ct);
        return result.IsSuccess ? ServiceResult.Ok(result.Value! with { Node = Map(result.Value.Node) }) : MapError(result);
    }

    public async Task<ServiceResult<IReadOnlyList<SnapshotEntry>>> GetSnapshotAsync(string path, CancellationToken ct, int? depth = null) =>
        Inner(path) is { } inner ? MapEntries(await _spaces.GetSnapshotAsync(SpaceId, inner, ct, depth)) : InvalidPath<IReadOnlyList<SnapshotEntry>>();

    public async Task<ServiceResult<UsageResponse>> GetUsageAsync(string path, CancellationToken ct) =>
        Inner(path) is { } inner ? await _spaces.GetUsageAsync(SpaceId, inner, ct) : InvalidPath<UsageResponse>();

    public async Task<ServiceResult<IReadOnlyList<SnapshotEntry>>> SearchAsync(string path, string query, int limit, CancellationToken ct) =>
        Inner(path) is { } inner ? MapEntries(await _spaces.SearchAsync(SpaceId, inner, query, limit, ct)) : InvalidPath<IReadOnlyList<SnapshotEntry>>();

    public async Task<ServiceResult<IReadOnlyList<SnapshotEntry>>> RecentAsync(int limit, CancellationToken ct) =>
        MapEntries(await _spaces.RecentAsync(SpaceId, Root.Value, limit, ct));

    public async Task<ServiceResult<VersionsResponse>> ListVersionsAsync(string path, CancellationToken ct) =>
        Inner(path, allowRoot: false) is { } inner ? await _spaces.ListVersionsAsync(SpaceId, inner, ct) : InvalidPath<VersionsResponse>();

    public async Task<ServiceResult<CommitResponse>> RestoreVersionAsync(RestoreVersionRequest request, string actorUserId, CancellationToken ct) =>
        Inner(request.Path, allowRoot: false) is { } inner
            ? MapError(await _spaces.RestoreVersionAsync(SpaceId, request with { Path = inner }, actorUserId, ct))
            : InvalidPath<CommitResponse>();

    /// <summary>Gelöschtes aus dem sichtbaren Teilbaum, mit Pfaden relativ zur Wurzel.</summary>
    public async Task<ServiceResult<TrashResponse>> ListTrashAsync(CancellationToken ct)
    {
        var trash = await _spaces.ListTrashAsync(SpaceId, ct);
        if (!trash.IsSuccess || !IsScoped)
        {
            return trash;
        }

        return ServiceResult.Ok(new TrashResponse([.. trash.Value!.Entries
            .Select(e => (Entry: e, Path: Outer(e.OriginalPath)))
            .Where(p => !string.IsNullOrEmpty(p.Path))
            .Select(p => p.Entry with { OriginalPath = p.Path! })]));
    }

    public async Task<ServiceResult<CommitResponse>> RestoreFromTrashAsync(string nodeId, string actorUserId, CancellationToken ct)
    {
        if (IsScoped && !((await ListTrashAsync(ct)).Value?.Entries.Any(e => e.NodeId == nodeId) ?? false))
        {
            return ServiceResult.Fail<CommitResponse>(new ServiceError(ErrorCode.NotFound, "Nicht im Papierkorb."));
        }

        return MapError(await _spaces.RestoreFromTrashAsync(SpaceId, nodeId, actorUserId, ct));
    }

    public async Task<ServiceResult<NodeInfo>> LockAsync(string path, string actorUserId, CancellationToken ct) =>
        Inner(path, allowRoot: false) is { } inner ? MapNode(await _spaces.LockAsync(SpaceId, inner, actorUserId, ct)) : InvalidPath<NodeInfo>();

    public async Task<ServiceResult<NodeInfo>> UnlockAsync(string path, string actorUserId, bool canOverride, CancellationToken ct) =>
        Inner(path, allowRoot: false) is { } inner
            ? MapNode(await _spaces.UnlockAsync(SpaceId, inner, actorUserId, canOverride, ct))
            : InvalidPath<NodeInfo>();

    /// <summary>Ein lebender Knoten über seine Id (Favoriten, Labels); außerhalb des Teilbaums gibt es ihn für diesen View nicht.</summary>
    public async Task<ServiceResult<NodeInfo>> GetNodeByIdAsync(string nodeId, CancellationToken ct)
    {
        var node = await _spaces.GetNodeByIdAsync(SpaceId, nodeId, ct);
        if (!node.IsSuccess)
        {
            return node;
        }

        return string.IsNullOrEmpty(Outer(node.Value!.Path))
            ? ServiceResult.Fail<NodeInfo>(new ServiceError(ErrorCode.NotFound, "Element existiert nicht."))
            : ServiceResult.Ok(Map(node.Value));
    }

    public async Task<ServiceResult<IReadOnlyList<ArchiveEntry>>> PlanArchiveAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var inner = paths.Select(p => Inner(p, allowRoot: false)).ToList();
        return inner.Any(p => p is null)
            ? InvalidPath<IReadOnlyList<ArchiveEntry>>()
            : await _spaces.PlanArchiveAsync(SpaceId, [.. inner.Select(p => p!)], ct);
    }

    /// <summary>
    /// Feed eines Teilbaums: Einträge darin mit umgerechneten Pfaden; Verschieben hinaus wird zum Löschen, eine Datei herein zum Anlegen.
    /// Ein Ordner, der hereingeschoben wird, oder eine Verschiebung der Wurzel selbst ergibt <c>cursor_expired</c>: der Client liest dann den
    /// Snapshot neu, statt einen halben Zustand zu sehen.
    /// </summary>
    private ServiceResult<ChangesResponse> Filter(ChangesResponse page)
    {
        var visible = new List<ChangeEntry>();
        foreach (var change in page.Changes)
        {
            if (change.Op is ChangeOps.Move or ChangeOps.Delete && (Covers(change.Path) || Covers(change.OldPath)))
            {
                return Expired();
            }

            var path = Outer(change.Path);
            var oldPath = Outer(change.OldPath);
            if (change.Op == ChangeOps.Move && path is not null && oldPath is null)
            {
                if (change.Kind == NodeKinds.Directory)
                {
                    return Expired();
                }

                visible.Add(change with { Op = ChangeOps.Upsert, Path = path, OldPath = null });
            }
            else if (change.Op == ChangeOps.Move && path is null && oldPath is not null)
            {
                visible.Add(change with { Op = ChangeOps.Delete, Path = oldPath, OldPath = null });
            }
            else if (!string.IsNullOrEmpty(path))
            {
                visible.Add(change with { Path = path, OldPath = oldPath });
            }
        }

        return ServiceResult.Ok(new ChangesResponse(visible, page.HasMore, page.NextCursor));
    }

    /// <summary>Betrifft der Pfad die Wurzel selbst oder einen ihrer Vorfahren?</summary>
    private bool Covers(string? path) =>
        path is not null && (path == Root.Value || Root.Value.StartsWith(path + "/", StringComparison.Ordinal));

    private static ServiceResult<ChangesResponse> Expired() =>
        ServiceResult.Fail<ChangesResponse>(new ServiceError(ErrorCode.CursorExpired, "Der geteilte Ordner hat sich verändert; bitte neu einlesen."));

    private NodeInfo Map(NodeInfo node) => node with { Path = Outer(node.Path) ?? string.Empty };

    private ServiceResult<NodeInfo> MapNode(ServiceResult<NodeInfo> result) =>
        result.IsSuccess ? ServiceResult.Ok(Map(result.Value!)) : MapError(result);

    private ServiceResult<IReadOnlyList<SnapshotEntry>> MapEntries(ServiceResult<IReadOnlyList<SnapshotEntry>> result) =>
        !result.IsSuccess || !IsScoped
            ? result
            : ServiceResult.Ok<IReadOnlyList<SnapshotEntry>>([.. result.Value!
                .Select(e => (Entry: e, Path: Outer(e.Path)))
                .Where(p => !string.IsNullOrEmpty(p.Path))
                .Select(p => p.Entry with { Path = p.Path! })]);

    /// <summary>Konflikte tragen den aktuellen Knoten mit; auch dessen Pfad gilt nach außen relativ zur Wurzel (außerhalb: weggelassen).</summary>
    private ServiceResult<T> MapError<T>(ServiceResult<T> result) =>
        result.Error is { Current: { } current } error && IsScoped
            ? ServiceResult.Fail<T>(error with { Current = Outer(current.Path) is { Length: > 0 } outer ? current with { Path = outer } : null })
            : result;

    private static ServiceResult<T> InvalidPath<T>() => ServiceResult.Fail<T>(new ServiceError(ErrorCode.InvalidPath, "Ungültiger Pfad."));
}
