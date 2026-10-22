using System.Text.Json;
using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Server.App.Ports;

namespace SyncDrive.Server.App.Spaces;

/// <param name="VersionsToKeep">Gespeicherte Versionen je Datei (Standard: 10).</param>
public sealed record SpaceServiceOptions(int VersionsToKeep = 10);

/// <summary>Use-Cases rund um den Dateibaum eines Space: Commit, Änderungsfeed, Blöcke, Versionen, Papierkorb, Sperren.</summary>
public sealed partial class SpaceService(
    ISpaceStore store,
    IBlockStore blocks,
    IChangeNotifier? notifier = null,
    TimeProvider? clock = null,
    SpaceServiceOptions? options = null,
    ISpaceQuotas? quotas = null)
{
    public const int MaxChangesPerPage = 1000;

    private readonly IChangeNotifier _notifier = notifier ?? new NullChangeNotifier();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SpaceServiceOptions _options = options ?? new SpaceServiceOptions();

    public Task<ServiceResult<CommitResponse>> CommitAsync(
        string spaceId, CommitRequest request, string? idempotencyKey, CancellationToken ct) =>
        CommitAsync(spaceId, request, idempotencyKey, actorUserId: null, ct);

    /// <param name="actorUserId">Wer handelt (Sperren, „erstellt von“ in Versionen); null bei Systemvorgängen.</param>
    public async Task<ServiceResult<CommitResponse>> CommitAsync(
        string spaceId, CommitRequest request, string? idempotencyKey, string? actorUserId, CancellationToken ct)
    {
        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<CommitResponse>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        var parsed = CommitCommand.Parse(request);
        if (parsed.Error is not null)
        {
            return ServiceResult.Fail<CommitResponse>(parsed.Error);
        }

        var command = parsed.Value!;
        if (command.Op == CommitOp.Put)
        {
            var missing = await FindMissingAsync(spaceId, command.Blocks, actorUserId, ct);
            if (missing.Count > 0)
            {
                return ServiceResult.Fail<CommitResponse>(new ServiceError(
                    ErrorCode.BlocksMissing, "Blöcke fehlen.", MissingBlocks: [.. missing.Select(h => h.Value)]));
            }
        }

        var quota = quotas is null || command.Op is not (CommitOp.Put or CommitOp.Copy) ? null : await quotas.QuotaOfAsync(spaceId, ct);
        var context = new CommitContext(actorUserId, _clock.GetUtcNow(), quota);
        var result = await store.InTransactionAsync(spaceId, tx => ApplyOnceAsync(tx, command, idempotencyKey, context), ct);
        if (result.IsSuccess)
        {
            _notifier.Notify(spaceId); // auch bei No-ops harmlos: Wartende lesen den Feed neu
        }

        return result;
    }

    public async Task<ServiceResult<CursorResponse>> GetLatestRevisionAsync(string spaceId, CancellationToken ct)
    {
        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<CursorResponse>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        var page = await store.ReadChangesAsync(spaceId, long.MaxValue, 1, ct);
        return ServiceResult.Ok(new CursorResponse(page.Latest));
    }

    /// <summary>
    /// Long-Poll: antwortet sofort, wenn es Änderungen nach <paramref name="after"/> gibt, sonst erst bei der nächsten
    /// Änderung oder nach <paramref name="timeout"/> mit leerer Liste.
    /// </summary>
    public async Task<ServiceResult<ChangesResponse>> WaitForChangesAsync(
        string spaceId, long after, int limit, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        while (true)
        {
            var seenVersion = _notifier.CurrentVersion(spaceId); // vor dem Lesen, damit kein Wecken verloren geht
            var result = await GetChangesAsync(spaceId, after, limit, ct);
            if (!result.IsSuccess || result.Value!.Changes.Count > 0)
            {
                return result;
            }

            try
            {
                await _notifier.WaitAsync(spaceId, seenVersion, timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return result;
            }
        }
    }

    public async Task<ServiceResult<ChangesResponse>> GetChangesAsync(
        string spaceId, long after, int limit, CancellationToken ct)
    {
        if (after < 0 || limit < 1)
        {
            return Fail<ChangesResponse>(ErrorCode.InvalidRequest, "after und limit müssen positiv sein.");
        }

        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<ChangesResponse>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        var page = await store.ReadChangesAsync(spaceId, after, Math.Min(limit, MaxChangesPerPage), ct);
        if (after < page.Floor || after > page.Latest)
        {
            return Fail<ChangesResponse>(ErrorCode.CursorExpired, "Cursor ungültig; Snapshot-Rescan nötig.");
        }

        var next = page.Changes.Count > 0 ? page.Changes[^1].Revision : after;
        return ServiceResult.Ok(new ChangesResponse([.. page.Changes.Select(ToEntry)], page.HasMore, next));
    }

    /// <summary>Knoten am Pfad samt Blockliste. Gelöschte Knoten kommen als Grabstein (<c>Deleted</c>) zurück, damit Clients ihre Revision kennen.</summary>
    public async Task<ServiceResult<NodeDetail>> GetNodeAsync(string spaceId, string path, CancellationToken ct)
    {
        if (!RelativePath.TryParse(path, out var parsed) || parsed.IsRoot)
        {
            return Fail<NodeDetail>(ErrorCode.InvalidPath, "Ungültiger Pfad.");
        }

        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<NodeDetail>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        return await store.InTransactionAsync(spaceId, async tx =>
        {
            if (await ResolveAsync(tx, parsed) is not { Leaf: { } leaf })
            {
                return Fail<NodeDetail>(ErrorCode.NotFound, "Knoten existiert nicht.");
            }

            var blockHashes = leaf.Deleted ? [] : leaf.Blocks.Select(b => b.Value).ToArray();
            return ServiceResult.Ok(new NodeDetail(ToInfo(leaf, parsed.Parent.Combine(leaf.Name)), blockHashes));
        }, ct);
    }

    /// <summary>
    /// Alle lebenden Knoten unterhalb von <paramref name="path"/> (leer = ganzer Space), sortiert nach Pfad. Ist <paramref name="path"/> eine
    /// Datei, besteht das Ergebnis nur aus ihr. Gedacht für den Rescan nach <c>cursor_expired</c> und für den ersten Abgleich.
    /// </summary>
    /// <param name="depth">Wie viele Ebenen unterhalb von <paramref name="path"/>; 1 = nur der direkte Inhalt (Ordnerbaum im Assistenten), null = alle.</param>
    public async Task<ServiceResult<IReadOnlyList<SnapshotEntry>>> GetSnapshotAsync(
        string spaceId, string path, CancellationToken ct, int? depth = null)
    {
        if (!RelativePath.TryParse(path, out var root))
        {
            return Fail<IReadOnlyList<SnapshotEntry>>(ErrorCode.InvalidPath, "Ungültiger Pfad.");
        }

        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<IReadOnlyList<SnapshotEntry>>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        return await store.InTransactionAsync(spaceId, async tx =>
        {
            var entries = new List<SnapshotEntry>();
            string? startId = null;
            if (!root.IsRoot)
            {
                if (await ResolveAsync(tx, root) is not { Leaf: { Deleted: false } start })
                {
                    return Fail<IReadOnlyList<SnapshotEntry>>(ErrorCode.NotFound, "Pfad existiert nicht.");
                }

                if (start.Kind == NodeKind.File)
                {
                    return ServiceResult.Ok<IReadOnlyList<SnapshotEntry>>([ToSnapshot(start, root)]);
                }

                startId = start.Id;
            }

            await CollectAsync(tx, startId, root, entries, depth ?? int.MaxValue);
            entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return ServiceResult.Ok<IReadOnlyList<SnapshotEntry>>(entries);
        }, ct);
    }

    /// <summary>Belegter Speicher des ganzen Space (aktuelle Dateien), ohne den Baum zu durchlaufen: Admin-Konsole und Kontingent.</summary>
    public async Task<long> GetUsedBytesAsync(string spaceId, CancellationToken ct) =>
        await store.SpaceExistsAsync(spaceId, ct) ? await store.InTransactionAsync(spaceId, tx => tx.SumLiveFileSizesAsync(), ct) : 0;

    /// <summary>Summe der Dateigrößen und Anzahl der Dateien unterhalb von <paramref name="path"/> (aktuelle Fassungen, ohne Versionen und Papierkorb).</summary>
    public async Task<ServiceResult<UsageResponse>> GetUsageAsync(string spaceId, string path, CancellationToken ct)
    {
        var snapshot = await GetSnapshotAsync(spaceId, path, ct);
        if (!snapshot.IsSuccess)
        {
            return ServiceResult.Fail<UsageResponse>(snapshot.Error!);
        }

        var files = snapshot.Value!.Where(e => e.Kind == NodeKinds.File).ToList();
        return ServiceResult.Ok(new UsageResponse(files.Sum(f => f.Size), files.Count));
    }

    private static async Task CollectAsync(ISpaceTransaction tx, string? parentId, RelativePath parentPath, List<SnapshotEntry> into, int depth)
    {
        foreach (var child in await tx.ListLiveChildrenAsync(parentId))
        {
            var path = parentPath.Combine(child.Name);
            into.Add(ToSnapshot(child, path));
            if (child.Kind == NodeKind.Directory && depth > 1)
            {
                await CollectAsync(tx, child.Id, path, into, depth - 1);
            }
        }
    }

    private static SnapshotEntry ToSnapshot(NodeRecord n, RelativePath path) =>
        new(n.Id, path.Value, KindText(n.Kind), n.Size, n.MTime, n.Hash?.Value, n.Revision);

    private static ServiceResult<T> Fail<T>(string code, string message) =>
        ServiceResult.Fail<T>(new ServiceError(code, message));

    private async Task<ServiceResult<CommitResponse>> ApplyOnceAsync(
        ISpaceTransaction tx, CommitCommand command, string? idempotencyKey, CommitContext ctx)
    {
        if (idempotencyKey is not null && await tx.GetIdempotentResponseAsync(idempotencyKey) is { } stored)
        {
            return ServiceResult.Ok(JsonSerializer.Deserialize<CommitResponse>(stored)!);
        }

        // vor dem Schreiben prüfen: der Store übernimmt die Transaktion auch, wenn das Ergebnis ein Fehler ist
        if (ctx.Quota is { } quota && await GrowthAsync(tx, command) is > 0 and var growth && await tx.SumLiveFileSizesAsync() + growth > quota)
        {
            return ServiceResult.Fail<CommitResponse>(new ServiceError(ErrorCode.QuotaExceeded, "Das Speicherkontingent ist ausgeschöpft."));
        }

        var result = command.Op switch
        {
            CommitOp.Put => await PutAsync(tx, command, ctx),
            CommitOp.Mkdir => await MkdirAsync(tx, command),
            CommitOp.Delete => await DeleteAsync(tx, command, ctx),
            CommitOp.Copy => await CopyAsync(tx, command, ctx),
            _ => await MoveAsync(tx, command, ctx),
        };

        if (result.IsSuccess && idempotencyKey is not null)
        {
            await tx.PutIdempotentResponseAsync(idempotencyKey, JsonSerializer.Serialize(result.Value));
        }

        return result;
    }

    /// <summary>Um wie viel der belegte Speicher wächst: neue Größe minus alte bei <c>put</c>, der kopierte Teilbaum bei <c>copy</c>.</summary>
    private static async Task<long> GrowthAsync(ISpaceTransaction tx, CommitCommand c)
    {
        var target = await ResolveAsync(tx, c.Path);
        return c.Op switch
        {
            CommitOp.Put => c.Size - (target is { Leaf: { Deleted: false, Kind: NodeKind.File } old } ? old.Size : 0),
            CommitOp.Copy when target is { Leaf: { Deleted: false } source } => await SubtreeSizeAsync(tx, source),
            _ => 0,
        };
    }

    private static async Task<long> SubtreeSizeAsync(ISpaceTransaction tx, NodeRecord node)
    {
        if (node.Kind == NodeKind.File)
        {
            return node.Size;
        }

        long total = 0;
        foreach (var child in await tx.ListLiveChildrenAsync(node.Id))
        {
            total += await SubtreeSizeAsync(tx, child);
        }

        return total;
    }

    private async Task<ServiceResult<CommitResponse>> PutAsync(ISpaceTransaction tx, CommitCommand c, CommitContext ctx)
    {
        if (await ResolveAsync(tx, c.Path) is not { } at)
        {
            return Fail<CommitResponse>(ErrorCode.ParentMissing, "Übergeordneter Ordner fehlt.");
        }

        var leaf = at.Leaf;
        if (leaf is { Deleted: false, Kind: NodeKind.Directory })
        {
            return Conflict(ErrorCode.TypeMismatch, "Am Pfad liegt ein Ordner.", leaf, c.Path);
        }

        if (leaf is { Deleted: false } && leaf.Hash == c.FileHash && leaf.Size == c.Size)
        {
            return Done(leaf.Revision, leaf.Id);
        }

        if (LockedForOthers(leaf, ctx))
        {
            return Conflict(ErrorCode.Locked, "Datei ist von jemand anderem gesperrt; bitte als neue Datei speichern.", leaf!, c.Path);
        }

        if ((leaf?.Revision ?? 0) > c.BaseRevision)
        {
            return Conflict(ErrorCode.RevisionConflict, "Knoten wurde inzwischen geändert.", leaf!, c.Path);
        }

        var revision = await tx.AllocateRevisionAsync();
        var node = new NodeRecord(
            leaf?.Id ?? NewId(), at.ParentId, leaf?.Name ?? at.Name, NodeKind.File, false, revision,
            c.Size, c.MTime, c.FileHash, c.Blocks, LockedBy: leaf?.LockedBy, LockedAt: leaf?.LockedAt);
        await tx.SaveAsync(node);
        await AddVersionAsync(tx, node, ctx);
        await tx.AppendChangeAsync(new ChangeRecord(
            revision, node.Id, ChangeOp.Upsert, c.Path.Parent.Combine(node.Name), null,
            NodeKind.File, c.Size, c.MTime, c.FileHash));
        return Done(revision, node.Id);
    }

    /// <summary>Jede neue Fassung wird eine Version; ältere als <see cref="SpaceServiceOptions.VersionsToKeep"/> fallen weg (Standard: 10).</summary>
    private async Task AddVersionAsync(ISpaceTransaction tx, NodeRecord node, CommitContext ctx)
    {
        await tx.AddVersionAsync(new NodeVersion(node.Id, node.Revision, node.Size, node.MTime, node.Hash, node.Blocks, ctx.Now, ctx.Actor));
        foreach (var old in (await tx.ListVersionsAsync(node.Id)).Skip(_options.VersionsToKeep))
        {
            await tx.RemoveVersionAsync(node.Id, old.Revision);
        }
    }

    private static async Task<ServiceResult<CommitResponse>> MkdirAsync(ISpaceTransaction tx, CommitCommand c)
    {
        if (await ResolveAsync(tx, c.Path) is not { } at)
        {
            return Fail<CommitResponse>(ErrorCode.ParentMissing, "Übergeordneter Ordner fehlt.");
        }

        var leaf = at.Leaf;
        if (leaf is { Deleted: false })
        {
            return leaf.Kind == NodeKind.Directory
                ? Done(leaf.Revision, leaf.Id)
                : Conflict(ErrorCode.TypeMismatch, "Am Pfad liegt eine Datei.", leaf, c.Path);
        }

        if ((leaf?.Revision ?? 0) > c.BaseRevision)
        {
            return Conflict(ErrorCode.RevisionConflict, "Knoten wurde inzwischen geändert.", leaf!, c.Path);
        }

        var revision = await tx.AllocateRevisionAsync();
        var node = new NodeRecord(
            leaf?.Id ?? NewId(), at.ParentId, leaf?.Name ?? at.Name, NodeKind.Directory, false, revision,
            0, c.MTime, null, []);
        await tx.SaveAsync(node);
        await tx.AppendChangeAsync(new ChangeRecord(
            revision, node.Id, ChangeOp.Upsert, c.Path.Parent.Combine(node.Name), null,
            NodeKind.Directory, 0, c.MTime, null));
        return Done(revision, node.Id);
    }

    private static async Task<ServiceResult<CommitResponse>> DeleteAsync(ISpaceTransaction tx, CommitCommand c, CommitContext ctx)
    {
        if (await ResolveAsync(tx, c.Path) is not { Leaf: { } leaf })
        {
            return Fail<CommitResponse>(ErrorCode.NotFound, "Knoten existiert nicht.");
        }

        if (leaf.Deleted)
        {
            return Done(leaf.Revision, leaf.Id);
        }

        if (leaf.Revision > c.BaseRevision)
        {
            return Conflict(ErrorCode.RevisionConflict, "Knoten wurde inzwischen geändert.", leaf, c.Path);
        }

        if (await FindForeignLockAsync(tx, leaf, ctx) is { } locked)
        {
            return Conflict(ErrorCode.Locked, "Enthält eine von jemand anderem gesperrte Datei.", locked, c.Path);
        }

        var revision = await tx.AllocateRevisionAsync();
        await MarkSubtreeDeletedAsync(tx, leaf, revision, ctx.Now);
        await tx.AppendChangeAsync(new ChangeRecord(
            revision, leaf.Id, ChangeOp.Delete, c.Path.Parent.Combine(leaf.Name), null,
            leaf.Kind, leaf.Size, leaf.MTime, leaf.Hash));
        return Done(revision, leaf.Id);
    }

    private static async Task<ServiceResult<CommitResponse>> MoveAsync(ISpaceTransaction tx, CommitCommand c, CommitContext ctx)
    {
        var target = c.NewPath!.Value;
        if (await ResolveAsync(tx, c.Path) is not { Leaf: { Deleted: false } source })
        {
            return Fail<CommitResponse>(ErrorCode.NotFound, "Quelle existiert nicht.");
        }

        if (source.Revision > c.BaseRevision)
        {
            return Conflict(ErrorCode.RevisionConflict, "Knoten wurde inzwischen geändert.", source, c.Path);
        }

        if (LockedForOthers(source, ctx))
        {
            return Conflict(ErrorCode.Locked, "Nur wer die Datei gesperrt hat, darf sie verschieben oder umbenennen.", source, c.Path);
        }

        if (c.Path.Value == target.Value)
        {
            return Done(source.Revision, source.Id);
        }

        if (target.IsSameOrDescendantOf(c.Path) && !target.EqualsIgnoreCase(c.Path))
        {
            return Fail<CommitResponse>(ErrorCode.InvalidPath, "Ziel liegt innerhalb der Quelle.");
        }

        if (await ResolveAsync(tx, target) is not { } dest)
        {
            return Fail<CommitResponse>(ErrorCode.ParentMissing, "Zielordner fehlt.");
        }

        if (dest.Leaf is { } existing && existing.Id != source.Id)
        {
            if (!existing.Deleted)
            {
                return Conflict(ErrorCode.AlreadyExists, "Am Ziel liegt bereits ein Knoten.", existing, target);
            }

            await tx.RemoveAsync(existing.Id); // Grabstein macht den Namen frei
        }

        var revision = await tx.AllocateRevisionAsync();
        var moved = source with { ParentId = dest.ParentId, Name = dest.Name, Revision = revision };
        await tx.SaveAsync(moved);
        await tx.AppendChangeAsync(new ChangeRecord(
            revision, moved.Id, ChangeOp.Move, target, c.Path, moved.Kind, moved.Size, moved.MTime, moved.Hash));
        return Done(revision, moved.Id);
    }

    /// <summary>Kopiert einen Knoten samt Teilbaum (B2 „Make a copy“/„Copy to“). Neue Knoten, dieselben Blöcke, keine Datenübertragung; Sperren werden nicht kopiert.</summary>
    private async Task<ServiceResult<CommitResponse>> CopyAsync(ISpaceTransaction tx, CommitCommand c, CommitContext ctx)
    {
        var target = c.NewPath!.Value;
        if (await ResolveAsync(tx, c.Path) is not { Leaf: { Deleted: false } source })
        {
            return Fail<CommitResponse>(ErrorCode.NotFound, "Quelle existiert nicht.");
        }

        if (target.IsSameOrDescendantOf(c.Path))
        {
            return Fail<CommitResponse>(ErrorCode.InvalidPath, "Ziel liegt innerhalb der Quelle.");
        }

        if (await ResolveAsync(tx, target) is not { } dest)
        {
            return Fail<CommitResponse>(ErrorCode.ParentMissing, "Zielordner fehlt.");
        }

        if (dest.Leaf is { } existing)
        {
            if (!existing.Deleted)
            {
                return Conflict(ErrorCode.AlreadyExists, "Am Ziel liegt bereits ein Knoten.", existing, target);
            }

            await tx.RemoveAsync(existing.Id);
        }

        var root = await CopyNodeAsync(tx, source, dest.ParentId, dest.Name, target, ctx);
        return Done(root.Revision, root.Id);
    }

    private async Task<NodeRecord> CopyNodeAsync(
        ISpaceTransaction tx, NodeRecord source, string? parentId, string name, RelativePath path, CommitContext ctx)
    {
        var revision = await tx.AllocateRevisionAsync();
        var copy = source with { Id = NewId(), ParentId = parentId, Name = name, Revision = revision, LockedBy = null, LockedAt = null };
        await tx.SaveAsync(copy);
        if (copy.Kind == NodeKind.File)
        {
            await AddVersionAsync(tx, copy, ctx);
        }

        await tx.AppendChangeAsync(new ChangeRecord(revision, copy.Id, ChangeOp.Upsert, path, null, copy.Kind, copy.Size, copy.MTime, copy.Hash));
        if (source.Kind == NodeKind.Directory)
        {
            foreach (var child in await tx.ListLiveChildrenAsync(source.Id))
            {
                await CopyNodeAsync(tx, child, copy.Id, child.Name, path.Combine(child.Name), ctx);
            }
        }

        return copy;
    }

    private static async Task MarkSubtreeDeletedAsync(ISpaceTransaction tx, NodeRecord node, long revision, DateTimeOffset now)
    {
        await tx.SaveAsync(node with { Deleted = true, Revision = revision, DeletedAt = now, LockedBy = null, LockedAt = null });
        if (node.Kind != NodeKind.Directory)
        {
            return;
        }

        foreach (var child in await tx.ListLiveChildrenAsync(node.Id))
        {
            await MarkSubtreeDeletedAsync(tx, child, revision, now);
        }
    }

    private static bool LockedForOthers(NodeRecord? node, CommitContext ctx) =>
        node is { Deleted: false, LockedBy: { } owner } && owner != ctx.Actor;

    /// <summary>Erste Datei im Teilbaum, die jemand anderes gesperrt hat.</summary>
    private static async Task<NodeRecord?> FindForeignLockAsync(ISpaceTransaction tx, NodeRecord node, CommitContext ctx)
    {
        if (LockedForOthers(node, ctx))
        {
            return node;
        }

        if (node.Kind == NodeKind.Directory)
        {
            foreach (var child in await tx.ListLiveChildrenAsync(node.Id))
            {
                if (await FindForeignLockAsync(tx, child, ctx) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>Läuft den Pfad ab. Null, wenn ein Vorfahre fehlt, gelöscht oder kein Ordner ist.</summary>
    private static async Task<Resolved?> ResolveAsync(ISpaceTransaction tx, RelativePath path)
    {
        string? parentId = null;
        var segments = path.Segments.ToList();
        foreach (var segment in segments.SkipLast(1))
        {
            var folder = await tx.FindChildAsync(parentId, segment);
            if (folder is not { Deleted: false, Kind: NodeKind.Directory })
            {
                return null;
            }

            parentId = folder.Id;
        }

        var name = segments[^1];
        return new Resolved(parentId, name, await tx.FindChildAsync(parentId, name));
    }

    private static string NewId() => "n_" + Guid.NewGuid().ToString("N");

    private static ServiceResult<CommitResponse> Done(long revision, string nodeId) =>
        ServiceResult.Ok(new CommitResponse(revision, nodeId));

    private static ServiceResult<CommitResponse> Conflict(string code, string message, NodeRecord current, RelativePath path) =>
        ServiceResult.Fail<CommitResponse>(new ServiceError(code, message, ToInfo(current, path)));

    private static NodeInfo ToInfo(NodeRecord n, RelativePath path) => new(
        n.Id, path.Value, KindText(n.Kind), n.Size, n.MTime, n.Hash?.Value, n.Revision, n.Deleted, n.LockedBy);

    private static ChangeEntry ToEntry(ChangeRecord c) => new(
        c.Revision, c.NodeId,
        c.Op switch
        {
            ChangeOp.Upsert => ChangeOps.Upsert,
            ChangeOp.Delete => ChangeOps.Delete,
            ChangeOp.Lock => ChangeOps.Lock,
            _ => ChangeOps.Move,
        },
        c.Path.Value, c.OldPath?.Value, KindText(c.Kind), c.Size, c.MTime, c.Hash?.Value);

    private static string KindText(NodeKind kind) => kind == NodeKind.File ? NodeKinds.File : NodeKinds.Directory;

    private sealed record Resolved(string? ParentId, string Name, NodeRecord? Leaf);

    /// <param name="Quota">Kontingent des Space für diesen Commit; null = unbegrenzt.</param>
    private sealed record CommitContext(string? Actor, DateTimeOffset Now, long? Quota = null);
}
