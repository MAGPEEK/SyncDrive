using SyncDrive.Contracts;

namespace SyncDrive.Server.App.Spaces;

/// <summary>
/// Suche und „Zuletzt“ für die Weboberfläche (M6.2). Beide durchlaufen den Snapshot des Space; für die Größen eines Heim-NAS genügt das.
/// Ein Index lohnt erst, wenn Spaces mit sehr vielen Dateien das messbar langsam machen.
/// </summary>
public sealed partial class SpaceService
{
    public const int MaxFindResults = 200;

    /// <summary>Einträge unter <paramref name="path"/>, deren Name <paramref name="query"/> enthält (ohne Rücksicht auf Groß- und Kleinschreibung).</summary>
    public async Task<ServiceResult<IReadOnlyList<SnapshotEntry>>> SearchAsync(
        string spaceId, string path, string query, int limit, CancellationToken ct)
    {
        var term = query?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return Fail<IReadOnlyList<SnapshotEntry>>(ErrorCode.InvalidRequest, "Suchbegriff fehlt.");
        }

        var snapshot = await GetSnapshotAsync(spaceId, path, ct);
        if (!snapshot.IsSuccess)
        {
            return snapshot;
        }

        return ServiceResult.Ok<IReadOnlyList<SnapshotEntry>>([.. snapshot.Value!
            .Where(e => NameOf(e.Path).Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Kind == NodeKinds.Directory ? 0 : 1)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, MaxFindResults))]);
    }

    /// <summary>Zuletzt geänderte Dateien unter <paramref name="path"/> (leer = ganzer Space), neueste zuerst (nach Änderung, Zugriffe werden nicht erfasst).</summary>
    public async Task<ServiceResult<IReadOnlyList<SnapshotEntry>>> RecentAsync(string spaceId, string path, int limit, CancellationToken ct)
    {
        var snapshot = await GetSnapshotAsync(spaceId, path, ct);
        if (!snapshot.IsSuccess)
        {
            return snapshot;
        }

        return ServiceResult.Ok<IReadOnlyList<SnapshotEntry>>([.. snapshot.Value!
            .Where(e => e.Kind == NodeKinds.File)
            .OrderByDescending(e => e.MTime)
            .ThenByDescending(e => e.Revision)
            .Take(Math.Clamp(limit, 1, MaxFindResults))]);
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}
