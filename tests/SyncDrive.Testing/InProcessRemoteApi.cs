using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Testing;

/// <summary>
/// <see cref="IRemoteApi"/> direkt auf dem <see cref="SpaceService"/>: echte Server-Logik ohne HTTP. Für Szenario-Tests mit mehreren Clients.
/// <see cref="Offline"/> simuliert einen Netzausfall, <see cref="UploadedBlocks"/> zählt tatsächlich gesendete Blöcke.
/// </summary>
public sealed class InProcessRemoteApi(SpaceService service, string spaceId) : IRemoteApi
{
    public bool Offline { get; set; }

    /// <summary>Simuliert einen Abbruch: Nach so vielen erfolgreich hochgeladenen Blöcken scheitert jeder weitere mit Netzfehler.</summary>
    public int? FailUploadsAfter { get; set; }

    public ConcurrentQueue<ContentHash> UploadedBlocks { get; } = new();

    /// <summary>Alle erfolgreich angenommenen Commits, in Reihenfolge.</summary>
    public ConcurrentQueue<CommitRequest> Commits { get; } = new();

    public async Task<Result<RemoteNode?>> GetNodeAsync(RelativePath path, CancellationToken ct)
    {
        if (Offline)
        {
            return NetworkDown<RemoteNode?>();
        }

        var result = await service.GetNodeAsync(spaceId, path.Value, ct);
        if (result.Error is { Code: ErrorCode.NotFound })
        {
            return Result.Ok<RemoteNode?>(null);
        }

        return result.IsSuccess ? Result.Ok<RemoteNode?>(RemoteMapping.ToRemoteNode(result.Value!)) : Failed<RemoteNode?>(result.Error!);
    }

    public async Task<Result<IReadOnlyList<SnapshotEntry>>> GetSnapshotAsync(RelativePath root, CancellationToken ct)
    {
        if (Offline)
        {
            return NetworkDown<IReadOnlyList<SnapshotEntry>>();
        }

        var result = await service.GetSnapshotAsync(spaceId, root.Value, ct);
        if (result.Error is { Code: ErrorCode.NotFound })
        {
            return Result.Ok<IReadOnlyList<SnapshotEntry>>([]);
        }

        return Map(result);
    }

    public async Task<Result<long>> GetLatestRevisionAsync(CancellationToken ct)
    {
        if (Offline)
        {
            return NetworkDown<long>();
        }

        var result = await service.GetLatestRevisionAsync(spaceId, ct);
        return result.IsSuccess ? Result.Ok(result.Value!.Latest) : Failed<long>(result.Error!);
    }

    public async Task<Result<ChangesResponse>> GetChangesAsync(long after, int limit, TimeSpan? wait, CancellationToken ct)
    {
        if (Offline)
        {
            return NetworkDown<ChangesResponse>();
        }

        return Map(wait is { } timeout
            ? await service.WaitForChangesAsync(spaceId, after, limit, timeout, ct)
            : await service.GetChangesAsync(spaceId, after, limit, ct));
    }

    public async Task<Result<IReadOnlyList<ContentHash>>> MissingBlocksAsync(IReadOnlyList<ContentHash> hashes, CancellationToken ct)
    {
        if (Offline)
        {
            return NetworkDown<IReadOnlyList<ContentHash>>();
        }

        var result = await service.MissingBlocksAsync(spaceId, hashes.Select(h => h.Value), uploaderId: null, ct); // ohne Benutzer: nur der Speicher zählt
        return result.IsSuccess
            ? Result.Ok<IReadOnlyList<ContentHash>>([.. result.Value!.Missing.Select(ContentHash.Parse)])
            : Failed<IReadOnlyList<ContentHash>>(result.Error!);
    }

    public async Task<Result<bool>> UploadBlockAsync(ContentHash hash, Stream content, CancellationToken ct)
    {
        if (Offline)
        {
            return NetworkDown<bool>();
        }

        if (FailUploadsAfter is { } limit && UploadedBlocks.Count >= limit)
        {
            return NetworkDown<bool>(); // Verbindung bricht mitten im Upload ab
        }

        var result = await service.PutBlockAsync(hash.Value, content, uploaderId: null, ct);
        if (result.IsSuccess)
        {
            UploadedBlocks.Enqueue(hash);
        }

        return Map(result);
    }

    public async Task<Result<Stream>> DownloadBlockAsync(ContentHash hash, CancellationToken ct) =>
        Offline ? NetworkDown<Stream>() : Map(await service.OpenBlockAsync(spaceId, hash.Value, ct));

    public async Task<Result<CommitResponse>> CommitAsync(CommitRequest request, string? idempotencyKey, CancellationToken ct)
    {
        if (Offline)
        {
            return NetworkDown<CommitResponse>();
        }

        var result = await service.CommitAsync(spaceId, request, idempotencyKey, ct);
        if (result.IsSuccess)
        {
            Commits.Enqueue(request);
        }

        return Map(result);
    }


    private static Result<T> Map<T>(ServiceResult<T> result) =>
        result.IsSuccess ? Result.Ok(result.Value!) : Failed<T>(result.Error!);

    private static Result<T> Failed<T>(ServiceError error) => Result.Fail<T>(RemoteErrors.From(error.Code, error.Message));

    private static Result<T> NetworkDown<T>() =>
        Result.Fail<T>(SyncErrorKind.Transient, RemoteErrors.Network, "Server nicht erreichbar (simuliert).");
}

/// <summary>Basis-Speicher im Arbeitsspeicher, Pfade ohne Beachtung der Schreibweise (wie unter Windows).</summary>
public sealed class InMemorySessionState : ISessionState
{
    private readonly ConcurrentDictionary<string, SyncedNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private long _cursor;

    public IReadOnlyCollection<SyncedNode> All => [.. _nodes.Values];

    public Task<SyncedNode?> GetAsync(RelativePath path, CancellationToken ct) =>
        Task.FromResult(_nodes.GetValueOrDefault(path.Value));

    public Task SaveAsync(SyncedNode node, CancellationToken ct)
    {
        _nodes[node.Path.Value] = node;
        return Task.CompletedTask;
    }

    public Task RemoveSubtreeAsync(RelativePath path, CancellationToken ct)
    {
        foreach (var key in _nodes.Keys.Where(k => RelativePath.Parse(k).IsSameOrDescendantOf(path)).ToList())
        {
            _nodes.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SyncedNode> ListAsync(RelativePath root, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var node in _nodes.Values.Where(n => n.Path.IsSameOrDescendantOf(root) && !n.Path.EqualsIgnoreCase(root)).OrderBy(n => n.Path.Value, StringComparer.Ordinal))
        {
            yield return node;
        }

        await Task.CompletedTask;
    }

    public Task<long> GetFeedCursorAsync(CancellationToken ct) => Task.FromResult(Interlocked.Read(ref _cursor));

    public Task SetFeedCursorAsync(long revision, CancellationToken ct)
    {
        Interlocked.Exchange(ref _cursor, revision);
        return Task.CompletedTask;
    }
}

public sealed class RecordingActivityLog : IActivityLog
{
    public ConcurrentQueue<ActivityEntry> Entries { get; } = new();

    public Task AppendAsync(ActivityEntry entry, CancellationToken ct)
    {
        Entries.Enqueue(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActivityEntry>> RecentAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActivityEntry>>([.. Entries.Reverse().Take(limit)]);
}
