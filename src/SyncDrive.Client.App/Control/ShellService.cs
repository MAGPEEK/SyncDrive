using System.Security.Cryptography;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Contracts;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Control;

/// <summary>
/// Dateistatus im Explorer: aktuell (auch „wartet“), in Arbeit, ausgeschlossen, nur lesend; dazu Fehler.
/// </summary>
public enum PathStatus
{
    UpToDate,
    Syncing,
    Excluded,
    ReadOnly,
    Error,
}

/// <summary>Ein Pfad in einem Sync-Ordner: zu welcher Aufgabe er gehört und wie es um ihn steht.</summary>
/// <param name="IsTaskRoot">Der Sync-Ordner selbst (kann nicht abgewählt werden).</param>
public sealed record PathInfo(string TaskId, string RelativePath, bool IsDirectory, PathStatus Status, bool IsTaskRoot);

/// <summary>Frühere Version für „Browse previous versions“.</summary>
public sealed record FileVersion(long Revision, long Size, DateTimeOffset MTime, DateTimeOffset CreatedAt, string? CreatedBy, bool IsCurrent);

public sealed record ShellResult(bool Succeeded, string? ErrorCode = null, string? Message = null)
{
    public static ShellResult Ok { get; } = new(true);
}

public static class ShellErrors
{
    public const string NotInSyncFolder = "not_in_sync_folder";
    public const string NotPossible = "not_possible";
    public const string Unreachable = "unreachable";
    public const string Corrupt = "corrupt";
}

/// <summary>Was das Explorer-Menü „SyncDrive“ anbietet; der Daemon setzt es um, die Oberfläche ruft es über die Pipe auf.</summary>
public interface IShellActions
{
    /// <summary>Null, wenn der Pfad in keinem Sync-Ordner liegt.</summary>
    Task<PathInfo?> GetPathInfoAsync(string localPath, CancellationToken ct);

    Task<IReadOnlyList<FileVersion>> ListVersionsAsync(string localPath, CancellationToken ct);

    /// <summary>Lädt eine frühere Version unter einem neuen Namen herunter (der Nutzer wählt Name und Ort).</summary>
    Task<ShellResult> SaveVersionAsAsync(string localPath, long revision, string targetPath, CancellationToken ct);

    /// <summary>Macht die Version auf dem Server wieder zur aktuellen; der Sync holt sie dann wie jede Änderung.</summary>
    Task<ShellResult> RestoreVersionAsync(string localPath, long revision, CancellationToken ct);

    Task<ShellResult> LockAsync(string localPath, LockAction action, CancellationToken ct);

    /// <summary>„Diesen Ordner nicht mehr synchronisieren“ bzw. wieder synchronisieren (Sync Rules → Folder).</summary>
    Task<ShellResult> SetFolderSyncedAsync(string localPath, bool synced, CancellationToken ct);
}

public sealed class ShellService(
    IClientConfigStore config,
    ICredentialStore credentials,
    IServerAccess server,
    DaemonController controller,
    IClientSetup setup) : IShellActions
{
    public async Task<PathInfo?> GetPathInfoAsync(string localPath, CancellationToken ct)
    {
        if (await LocateAsync(localPath, ct) is not { } found)
        {
            return null;
        }

        var (task, relative) = found;
        var isDirectory = Directory.Exists(localPath);
        return new PathInfo(task.Id, relative.Value, isDirectory, Status(task, relative, localPath, isDirectory), relative.IsRoot);
    }

    public async Task<IReadOnlyList<FileVersion>> ListVersionsAsync(string localPath, CancellationToken ct)
    {
        if (await RemoteAsync(localPath, ct) is not { } remote)
        {
            return [];
        }

        var versions = await server.ListVersionsAsync(remote.Login, remote.Task.SpaceId, remote.Path, ct);
        var current = await server.GetNodeInfoAsync(remote.Login, remote.Task.SpaceId, remote.Path, ct);
        var currentRevision = current.Value?.Revision;
        return versions.IsSuccess
            ? [.. versions.Value!.OrderByDescending(v => v.Revision)
                .Select(v => new FileVersion(v.Revision, v.Size, v.MTime, v.CreatedAt, v.CreatedBy, v.Revision == currentRevision))]
            : [];
    }

    public async Task<ShellResult> SaveVersionAsAsync(string localPath, long revision, string targetPath, CancellationToken ct)
    {
        if (await RemoteAsync(localPath, ct) is not { } remote)
        {
            return new ShellResult(false, ShellErrors.NotInSyncFolder);
        }

        var versions = await server.ListVersionsAsync(remote.Login, remote.Task.SpaceId, remote.Path, ct);
        if (!versions.IsSuccess)
        {
            return Failed(versions.Error!);
        }

        if (versions.Value!.FirstOrDefault(v => v.Revision == revision) is not { Blocks: { } blocks } version)
        {
            return new ShellResult(false, ErrorCode.NotFound, "Version existiert nicht (mehr).");
        }

        var temp = targetPath + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            await using (var output = File.Create(temp))
            {
                foreach (var hash in blocks)
                {
                    var block = await server.DownloadBlockAsync(remote.Login, remote.Task.SpaceId, hash, ct);
                    if (!block.IsSuccess)
                    {
                        return Failed(block.Error!);
                    }

                    await using var content = block.Value!;
                    using var buffer = new MemoryStream();
                    await content.CopyToAsync(buffer, ct);
                    if (!hash.Equals("sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))), StringComparison.OrdinalIgnoreCase))
                    {
                        return new ShellResult(false, ShellErrors.Corrupt, "Block passt nicht zu seinem Hash.");
                    }

                    buffer.Position = 0;
                    await buffer.CopyToAsync(output, ct);
                }
            }

            File.Move(temp, targetPath, overwrite: true);
            File.SetLastWriteTimeUtc(targetPath, version.MTime.UtcDateTime);
            return ShellResult.Ok;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task<ShellResult> RestoreVersionAsync(string localPath, long revision, CancellationToken ct)
    {
        if (await RemoteAsync(localPath, ct) is not { } remote)
        {
            return new ShellResult(false, ShellErrors.NotInSyncFolder);
        }

        var node = await server.GetNodeInfoAsync(remote.Login, remote.Task.SpaceId, remote.Path, ct);
        if (node.Value is not { } current)
        {
            return node.IsSuccess ? new ShellResult(false, ErrorCode.NotFound) : Failed(node.Error!);
        }

        var restored = await server.RestoreVersionAsync(remote.Login, remote.Task.SpaceId, remote.Path, revision, current.Revision, ct);
        return restored.IsSuccess ? ShellResult.Ok : Failed(restored.Error!);
    }

    public async Task<ShellResult> LockAsync(string localPath, LockAction action, CancellationToken ct)
    {
        if (await RemoteAsync(localPath, ct) is not { } remote)
        {
            return new ShellResult(false, ShellErrors.NotInSyncFolder);
        }

        var result = await server.LockAsync(remote.Login, remote.Task.SpaceId, remote.Path, action, ct);
        return result.IsSuccess ? ShellResult.Ok : Failed(result.Error!);
    }

    public async Task<ShellResult> SetFolderSyncedAsync(string localPath, bool synced, CancellationToken ct)
    {
        if (await LocateAsync(localPath, ct) is not { } found)
        {
            return new ShellResult(false, ShellErrors.NotInSyncFolder);
        }

        var (task, relative) = found;
        if (relative.IsRoot)
        {
            return new ShellResult(false, ShellErrors.NotPossible, "Der Sync-Ordner selbst lässt sich nur als ganze Aufgabe entfernen.");
        }

        var filters = task.Filters ?? new TaskFilterSettings();
        var excluded = (filters.ExcludedFolders ?? []).Where(f => !f.EqualsIgnoreCase(relative)).ToList();
        if (!synced)
        {
            excluded.Add(relative);
        }

        var outcome = await setup.UpdateTaskAsync(task with { Filters = filters with { ExcludedFolders = excluded } }, ct);
        return outcome.Succeeded ? ShellResult.Ok : new ShellResult(false, outcome.ErrorCode ?? ShellErrors.NotPossible, outcome.Message);
    }

    private PathStatus Status(SyncTaskDefinition task, RelativePath relative, string localPath, bool isDirectory)
    {
        if (!relative.IsRoot && IsExcluded(task, relative, localPath, isDirectory))
        {
            return PathStatus.Excluded;
        }

        if (controller.StatusOf(task.Id) is { } status)
        {
            bool Touches(string? path) => path is not null
                && (relative.IsRoot || path.Equals(relative.Value, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(relative.Value + "/", StringComparison.OrdinalIgnoreCase));

            if (status.FailingPaths.Any(Touches))
            {
                return PathStatus.Error;
            }

            if (Touches(status.CurrentPath))
            {
                return PathStatus.Syncing;
            }
        }

        return task.Direction == SyncDirection.DownloadOnly ? PathStatus.ReadOnly : PathStatus.UpToDate;
    }

    /// <summary>Ausgeschlossen, wenn der Pfad selbst oder ein Elternordner am Filter scheitert (Standardfilter, Sync Rules).</summary>
    private static bool IsExcluded(SyncTaskDefinition task, RelativePath relative, string localPath, bool isDirectory)
    {
        var filter = DefaultFilters.Create(task.Filters);
        var mtime = DateTimeOffset.UtcNow;
        var current = relative;
        var leaf = true;
        while (!current.IsRoot)
        {
            var state = leaf && !isDirectory
                ? NodeState.File(File.Exists(localPath) ? new FileInfo(localPath).Length : 0, mtime)
                : NodeState.Directory(mtime);
            if (!filter.Evaluate(current, state).Allowed)
            {
                return true;
            }

            current = current.Parent;
            leaf = false;
        }

        return false;
    }

    private async Task<(SyncTaskDefinition Task, RelativePath Relative)?> LocateAsync(string localPath, CancellationToken ct)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath));
        foreach (var task in await config.ListTasksAsync(ct))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.LocalPath));
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return (task, RelativePath.Root);
            }

            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && RelativePath.TryParse(Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/'), out var relative))
            {
                return (task, relative);
            }
        }

        return null;
    }

    private async Task<(SyncTaskDefinition Task, RelativePath Path, ServerLogin Login)?> RemoteAsync(string localPath, CancellationToken ct)
    {
        if (await LocateAsync(localPath, ct) is not { } found)
        {
            return null;
        }

        var (task, relative) = found;
        var connection = (await config.ListConnectionsAsync(ct)).FirstOrDefault(c => c.Id == task.ConnectionId);
        var token = connection is null ? null : await credentials.GetAsync(CredentialKeys.DeviceToken(connection.Id), ct);
        if (connection is null || token is null)
        {
            return null;
        }

        var remotePath = task.RemotePath.IsRoot ? relative
            : relative.IsRoot ? task.RemotePath
            : RelativePath.Parse(task.RemotePath.Value + "/" + relative.Value);
        return (task, remotePath, new ServerLogin(connection.ServerUrl, token, connection.PinnedCertificateSha256));
    }

    private static ShellResult Failed(Core.Results.SyncError error) =>
        new(false, error.Code == Sync.RemoteErrors.Network ? ShellErrors.Unreachable : error.Code, error.Message);
}
