using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Server.App.Ports;

namespace SyncDrive.Server.App.Spaces;

/// <summary>Ein Eintrag im ZIP: Name relativ zum gemeinsamen Ordner der Auswahl (Ordner enden mit <c>/</c>) und die Blöcke der Datei.</summary>
public sealed record ArchiveEntry(string Name, bool IsFolder, DateTimeOffset MTime, long Size, IReadOnlyList<string> Blocks);

/// <summary>„Herunterladen“ von Ordnern oder mehreren Elementen als ZIP (M6.3): der Server legt fest, was hineingehört; gepackt wird beim Senden.</summary>
public sealed partial class SpaceService
{
    public const int MaxArchiveSelection = 1000;

    /// <summary>
    /// Alles, was zu den gewählten Elementen gehört, in einem Zug gelesen (ein Stand, auch wenn währenddessen jemand ändert). Die Elemente
    /// müssen im selben Ordner liegen, wie bei einer Auswahl in der Liste; so bleiben die Namen im ZIP eindeutig.
    /// </summary>
    public async Task<ServiceResult<IReadOnlyList<ArchiveEntry>>> PlanArchiveAsync(
        string spaceId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count is 0 or > MaxArchiveSelection)
        {
            return Fail<IReadOnlyList<ArchiveEntry>>(ErrorCode.InvalidRequest, "Auswahl leer oder zu groß.");
        }

        var parsed = new List<RelativePath>();
        foreach (var path in paths)
        {
            if (!RelativePath.TryParse(path, out var p) || p.IsRoot)
            {
                return Fail<IReadOnlyList<ArchiveEntry>>(ErrorCode.InvalidPath, "Ungültiger Pfad.");
            }

            parsed.Add(p);
        }

        if (parsed.Select(p => p.Parent.Value).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return Fail<IReadOnlyList<ArchiveEntry>>(ErrorCode.InvalidRequest, "Die Elemente liegen nicht im selben Ordner.");
        }

        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<IReadOnlyList<ArchiveEntry>>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        return await store.InTransactionAsync(spaceId, async tx =>
        {
            var entries = new List<ArchiveEntry>();
            foreach (var path in parsed.DistinctBy(p => p.Value, StringComparer.Ordinal))
            {
                if (await ResolveAsync(tx, path) is not { Leaf: { Deleted: false } leaf })
                {
                    return Fail<IReadOnlyList<ArchiveEntry>>(ErrorCode.NotFound, "Element existiert nicht.");
                }

                await AddToArchiveAsync(tx, leaf, leaf.Name, entries);
            }

            return ServiceResult.Ok<IReadOnlyList<ArchiveEntry>>(entries);
        }, ct);
    }

    private static async Task AddToArchiveAsync(ISpaceTransaction tx, NodeRecord node, string name, List<ArchiveEntry> into)
    {
        if (node.Kind != NodeKind.Directory)
        {
            into.Add(new ArchiveEntry(name, IsFolder: false, node.MTime, node.Size, [.. node.Blocks.Select(b => b.Value)]));
            return;
        }

        into.Add(new ArchiveEntry(name + "/", IsFolder: true, node.MTime, 0, []));
        foreach (var child in (await tx.ListLiveChildrenAsync(node.Id)).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            await AddToArchiveAsync(tx, child, name + "/" + child.Name, into);
        }
    }
}
