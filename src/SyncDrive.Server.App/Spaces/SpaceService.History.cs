using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Server.App.Ports;

namespace SyncDrive.Server.App.Spaces;

/// <summary>Versionen (B3), Server-Papierkorb und Dateisperren (A15).</summary>
public sealed partial class SpaceService
{
    public async Task<ServiceResult<VersionsResponse>> ListVersionsAsync(string spaceId, string path, CancellationToken ct)
    {
        if (await CheckAsync(spaceId, path, ct) is { } invalid)
        {
            return ServiceResult.Fail<VersionsResponse>(invalid);
        }

        var parsed = RelativePath.Parse(path);
        return await store.InTransactionAsync(spaceId, async tx =>
        {
            if (await ResolveAsync(tx, parsed) is not { Leaf: { Deleted: false, Kind: NodeKind.File } file })
            {
                return Fail<VersionsResponse>(ErrorCode.NotFound, "Datei existiert nicht.");
            }

            var versions = await tx.ListVersionsAsync(file.Id);
            return ServiceResult.Ok(new VersionsResponse(
                [.. versions.Select(v => new VersionInfo(v.Revision, v.Size, v.MTime, v.Hash?.Value, v.CreatedAt, v.CreatedBy, [.. v.Blocks.Select(b => b.Value)]))]));
        }, ct);
    }

    /// <summary>Macht eine frühere Version zur aktuellen; das ist eine neue Fassung mit Server-Zeit, die alle Clients wie eine Änderung übernehmen.</summary>
    public async Task<ServiceResult<CommitResponse>> RestoreVersionAsync(
        string spaceId, RestoreVersionRequest request, string? actorUserId, CancellationToken ct)
    {
        if (await CheckAsync(spaceId, request.Path, ct) is { } invalid)
        {
            return ServiceResult.Fail<CommitResponse>(invalid);
        }

        var path = RelativePath.Parse(request.Path);
        var context = new CommitContext(actorUserId, _clock.GetUtcNow());
        var result = await store.InTransactionAsync(spaceId, async tx =>
        {
            if (await ResolveAsync(tx, path) is not { Leaf: { Deleted: false, Kind: NodeKind.File } file })
            {
                return Fail<CommitResponse>(ErrorCode.NotFound, "Datei existiert nicht.");
            }

            var version = (await tx.ListVersionsAsync(file.Id)).FirstOrDefault(v => v.Revision == request.Revision);
            if (version is null)
            {
                return Fail<CommitResponse>(ErrorCode.NotFound, "Version existiert nicht (mehr).");
            }

            var command = new CommitCommand(
                CommitOp.Put, path, request.BaseRevision, null, version.Blocks, version.Size, context.Now, version.Hash);
            return await PutAsync(tx, command, context);
        }, ct);
        NotifyOnSuccess(spaceId, result.IsSuccess);
        return result;
    }

    /// <summary>Oberste gelöschte Elemente (ein gelöschter Ordner erscheint einmal, nicht mit jedem Kind), neueste zuerst.</summary>
    public async Task<ServiceResult<TrashResponse>> ListTrashAsync(string spaceId, CancellationToken ct)
    {
        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<TrashResponse>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        return await store.InTransactionAsync(spaceId, async tx =>
        {
            var entries = new List<TrashEntry>();
            foreach (var node in await tx.ListDeletedAsync())
            {
                if (await PathOfParentAsync(tx, node.ParentId) is { } parentPath)
                {
                    entries.Add(new TrashEntry(
                        node.Id, node.Name, parentPath.Combine(node.Name).Value, KindText(node.Kind), node.Size, node.DeletedAt));
                }
            }

            return ServiceResult.Ok(new TrashResponse([.. entries.OrderByDescending(e => e.DeletedAt).ThenBy(e => e.OriginalPath, StringComparer.Ordinal)]));
        }, ct);
    }

    /// <summary>Stellt ein gelöschtes Element samt allem wieder her, was mit ihm gelöscht wurde (gleiche Revision), am ursprünglichen Ort.</summary>
    public async Task<ServiceResult<CommitResponse>> RestoreFromTrashAsync(
        string spaceId, string nodeId, string? actorUserId, CancellationToken ct)
    {
        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<CommitResponse>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        var result = await store.InTransactionAsync(spaceId, async tx =>
        {
            if (await tx.FindByIdAsync(nodeId) is not { Deleted: true } node)
            {
                return Fail<CommitResponse>(ErrorCode.NotFound, "Nicht im Papierkorb.");
            }

            if (await PathOfParentAsync(tx, node.ParentId) is not { } parentPath)
            {
                return Fail<CommitResponse>(ErrorCode.ParentMissing, "Der ursprüngliche Ordner existiert nicht mehr.");
            }

            if (await tx.FindChildAsync(node.ParentId, node.Name) is { Deleted: false } occupant && occupant.Id != node.Id)
            {
                return Conflict(ErrorCode.AlreadyExists, "Am ursprünglichen Ort liegt inzwischen etwas anderes.", occupant, parentPath.Combine(node.Name));
            }

            var revision = await RestoreSubtreeAsync(tx, node, parentPath.Combine(node.Name), node.Revision);
            return Done(revision, node.Id);
        }, ct);
        NotifyOnSuccess(spaceId, result.IsSuccess);
        return result;
    }

    /// <summary>Sperrt eine Datei für andere (A15). Wer sie gesperrt hat, darf weiter bearbeiten; die Knoten-Revision ändert sich nicht.</summary>
    public async Task<ServiceResult<NodeInfo>> LockAsync(string spaceId, string path, string actorUserId, CancellationToken ct) =>
        await ChangeLockAsync(spaceId, path, actorUserId, lockIt: true, canOverride: false, ct);

    /// <param name="canOverride">Manager und Besitzer dürfen fremde Sperren lösen.</param>
    public async Task<ServiceResult<NodeInfo>> UnlockAsync(
        string spaceId, string path, string actorUserId, bool canOverride, CancellationToken ct) =>
        await ChangeLockAsync(spaceId, path, actorUserId, lockIt: false, canOverride, ct);

    private async Task<ServiceResult<NodeInfo>> ChangeLockAsync(
        string spaceId, string path, string actor, bool lockIt, bool canOverride, CancellationToken ct)
    {
        if (await CheckAsync(spaceId, path, ct) is { } invalid)
        {
            return ServiceResult.Fail<NodeInfo>(invalid);
        }

        var parsed = RelativePath.Parse(path);
        var now = _clock.GetUtcNow();
        var changed = false;
        var result = await store.InTransactionAsync(spaceId, async tx =>
        {
            if (await ResolveAsync(tx, parsed) is not { Leaf: { Deleted: false } node })
            {
                return Fail<NodeInfo>(ErrorCode.NotFound, "Datei existiert nicht.");
            }

            if (node.Kind != NodeKind.File)
            {
                return Fail<NodeInfo>(ErrorCode.InvalidRequest, "Nur Dateien lassen sich sperren.");
            }

            NodeRecord updated;
            if (lockIt)
            {
                if (node.LockedBy is { } owner)
                {
                    return owner == actor
                        ? ServiceResult.Ok(ToInfo(node, parsed))
                        : ServiceResult.Fail<NodeInfo>(new ServiceError(ErrorCode.Locked, "Bereits von jemand anderem gesperrt.", ToInfo(node, parsed)));
                }

                updated = node with { LockedBy = actor, LockedAt = now };
            }
            else
            {
                if (node.LockedBy is null)
                {
                    return ServiceResult.Ok(ToInfo(node, parsed));
                }

                if (node.LockedBy != actor && !canOverride)
                {
                    return Fail<NodeInfo>(ErrorCode.Forbidden, "Nur wer gesperrt hat oder ein Manager darf entsperren.");
                }

                updated = node with { LockedBy = null, LockedAt = null };
            }

            await tx.SaveAsync(updated);
            var feedRevision = await tx.AllocateRevisionAsync();
            await tx.AppendChangeAsync(new ChangeRecord(
                feedRevision, node.Id, ChangeOp.Lock, parsed.Parent.Combine(node.Name), null, node.Kind, node.Size, node.MTime, node.Hash));
            changed = true;
            return ServiceResult.Ok(ToInfo(updated, parsed));
        }, ct);
        NotifyOnSuccess(spaceId, changed);
        return result;
    }

    /// <summary>Jeder wiederhergestellte Knoten bekommt eine eigene Revision (der Feed ist je Revision eindeutig). Liefert die des obersten.</summary>
    private static async Task<long> RestoreSubtreeAsync(ISpaceTransaction tx, NodeRecord node, RelativePath path, long deletedInRevision)
    {
        var revision = await tx.AllocateRevisionAsync();
        var restored = node with { Deleted = false, Revision = revision, DeletedAt = null };
        await tx.SaveAsync(restored);
        await tx.AppendChangeAsync(new ChangeRecord(revision, node.Id, ChangeOp.Upsert, path, null, node.Kind, node.Size, node.MTime, node.Hash));
        if (node.Kind == NodeKind.Directory)
        {
            foreach (var child in await tx.ListDeletedChildrenAsync(node.Id, deletedInRevision))
            {
                await RestoreSubtreeAsync(tx, child, path.Combine(child.Name), deletedInRevision);
            }
        }

        return revision;
    }

    /// <summary>Pfad eines lebenden Elternknotens (Wurzel = leerer Pfad); null, wenn ein Vorfahre gelöscht ist.</summary>
    private static async Task<RelativePath?> PathOfParentAsync(ISpaceTransaction tx, string? parentId)
    {
        var names = new Stack<string>();
        for (var id = parentId; id is not null;)
        {
            if (await tx.FindByIdAsync(id) is not { Deleted: false } ancestor)
            {
                return null;
            }

            names.Push(ancestor.Name);
            id = ancestor.ParentId;
        }

        var path = RelativePath.Root;
        while (names.TryPop(out var name))
        {
            path = path.Combine(name);
        }

        return path;
    }

    /// <summary>Aktueller Zustand und Pfad eines lebenden Knotens über seine stabile Id (Favoriten, Labels); NotFound, wenn gelöscht.</summary>
    public async Task<ServiceResult<NodeInfo>> GetNodeByIdAsync(string spaceId, string nodeId, CancellationToken ct)
    {
        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<NodeInfo>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        return await store.InTransactionAsync(spaceId, async tx =>
        {
            if (await tx.FindByIdAsync(nodeId) is not { Deleted: false } node
                || await PathOfParentAsync(tx, node.ParentId) is not { } parentPath)
            {
                return Fail<NodeInfo>(ErrorCode.NotFound, "Element existiert nicht mehr.");
            }

            return ServiceResult.Ok(ToInfo(node, parentPath.Combine(node.Name)));
        }, ct);
    }

    private async Task<ServiceError?> CheckAsync(string spaceId, string path, CancellationToken ct)
    {
        if (!RelativePath.TryParse(path, out var parsed) || parsed.IsRoot)
        {
            return new ServiceError(ErrorCode.InvalidPath, "Ungültiger Pfad.");
        }

        return await store.SpaceExistsAsync(spaceId, ct) ? null : new ServiceError(ErrorCode.SpaceNotFound, "Space unbekannt.");
    }

    private void NotifyOnSuccess(string spaceId, bool success)
    {
        if (success)
        {
            _notifier.Notify(spaceId);
        }
    }
}
