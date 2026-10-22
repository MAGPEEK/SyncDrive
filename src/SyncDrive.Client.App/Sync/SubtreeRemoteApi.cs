using SyncDrive.Client.App.Ports;
using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Sync;

/// <summary>
/// Eine Sync-Aufgabe gleicht nicht den ganzen Space ab, sondern einen Ordner darin (im Assistenten gewählt). Diese Hülle lässt die Engine
/// so arbeiten, als wäre der Ordner die Wurzel: Pfade werden beim Senden vorne ergänzt und beim Empfangen gekürzt, der Feed wird auf den
/// Ordner beschränkt. Verschiebungen über die Grenze werden zu „gelöscht“ (hinaus) bzw. „neu“ (herein).
/// </summary>
public sealed class SubtreeRemoteApi : IRemoteApi
{
    private readonly IRemoteApi _inner;
    private readonly RelativePath _root;

    public SubtreeRemoteApi(IRemoteApi inner, RelativePath root)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(root.IsRoot, true, nameof(root));
        _inner = inner;
        _root = root;
    }

    /// <summary>Ohne Unterordner die Schnittstelle selbst, sonst die Hülle.</summary>
    public static IRemoteApi For(IRemoteApi inner, RelativePath root) => root.IsRoot ? inner : new SubtreeRemoteApi(inner, root);

    public Task<Result<RemoteNode?>> GetNodeAsync(RelativePath path, CancellationToken ct) => _inner.GetNodeAsync(Outer(path), ct);

    public async Task<Result<IReadOnlyList<SnapshotEntry>>> GetSnapshotAsync(RelativePath root, CancellationToken ct)
    {
        var snapshot = await _inner.GetSnapshotAsync(Outer(root), ct);
        if (!snapshot.IsSuccess)
        {
            return snapshot;
        }

        var entries = new List<SnapshotEntry>(snapshot.Value!.Count);
        foreach (var entry in snapshot.Value)
        {
            if (Inner(entry.Path) is { IsRoot: false } path)
            {
                entries.Add(entry with { Path = path.Value });
            }
        }

        return Result.Ok<IReadOnlyList<SnapshotEntry>>(entries);
    }

    public Task<Result<long>> GetLatestRevisionAsync(CancellationToken ct) => _inner.GetLatestRevisionAsync(ct);

    public async Task<Result<ChangesResponse>> GetChangesAsync(long after, int limit, TimeSpan? wait, CancellationToken ct)
    {
        var page = await _inner.GetChangesAsync(after, limit, wait, ct);
        if (!page.IsSuccess)
        {
            return page;
        }

        // Der Cursor rückt über alle Einträge vor, auch über die gefilterten; sonst käme dieselbe Seite immer wieder.
        var changes = page.Value!.Changes.Select(Translate).OfType<ChangeEntry>().ToList();
        return Result.Ok(page.Value with { Changes = changes });
    }

    public Task<Result<IReadOnlyList<ContentHash>>> MissingBlocksAsync(IReadOnlyList<ContentHash> hashes, CancellationToken ct) =>
        _inner.MissingBlocksAsync(hashes, ct);

    public Task<Result<bool>> UploadBlockAsync(ContentHash hash, Stream content, CancellationToken ct) => _inner.UploadBlockAsync(hash, content, ct);

    public Task<Result<Stream>> DownloadBlockAsync(ContentHash hash, CancellationToken ct) => _inner.DownloadBlockAsync(hash, ct);

    public Task<Result<CommitResponse>> CommitAsync(CommitRequest request, string? idempotencyKey, CancellationToken ct) =>
        _inner.CommitAsync(
            request with
            {
                Path = Outer(RelativePath.Parse(request.Path)).Value,
                NewPath = request.NewPath is null ? null : Outer(RelativePath.Parse(request.NewPath)).Value,
            },
            idempotencyKey,
            ct);

    private ChangeEntry? Translate(ChangeEntry change)
    {
        var path = Inner(change.Path);
        var oldPath = change.OldPath is null ? null : Inner(change.OldPath);
        var inside = path is { IsRoot: false };
        var wasInside = oldPath is { IsRoot: false };

        if (change.Op == ChangeOps.Move && change.OldPath is not null)
        {
            return (wasInside, inside) switch
            {
                (true, true) => change with { Path = path!.Value.Value, OldPath = oldPath!.Value.Value },
                (true, false) => change with { Op = ChangeOps.Delete, Path = oldPath!.Value.Value, OldPath = null }, // hinaus verschoben
                (false, true) => change with { Op = ChangeOps.Upsert, Path = path!.Value.Value, OldPath = null }, // herein verschoben
                _ => null,
            };
        }

        return inside ? change with { Path = path!.Value.Value, OldPath = null } : null;
    }

    private RelativePath Outer(RelativePath path) => path.IsRoot ? _root : RelativePath.Parse(_root.Value + "/" + path.Value);

    /// <summary>Pfad relativ zum Aufgabenordner; null, wenn er außerhalb liegt. Groß-/Kleinschreibung wie auf dem Server egal.</summary>
    private RelativePath? Inner(string outer)
    {
        if (outer.Equals(_root.Value, StringComparison.OrdinalIgnoreCase))
        {
            return RelativePath.Root;
        }

        return outer.Length > _root.Value.Length + 1
            && outer.StartsWith(_root.Value, StringComparison.OrdinalIgnoreCase)
            && outer[_root.Value.Length] == '/'
                ? RelativePath.Parse(outer[(_root.Value.Length + 1)..])
                : null;
    }
}
