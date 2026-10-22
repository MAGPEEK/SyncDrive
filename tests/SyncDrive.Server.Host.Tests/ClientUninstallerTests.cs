using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Client.Daemon;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.Fs;
using SyncDrive.Infra.Sqlite.Client;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Deinstallation: Sync-Ordner abmelden, Arbeitsordner und Ablage löschen, Registry aufräumen; die Dateien des Benutzers bleiben.</summary>
public sealed class ClientUninstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncdrive-uninstall-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Everything_of_the_client_goes_but_the_synced_files_stay()
    {
        var paths = new ClientPaths(Path.Combine(_root, "profil"));
        paths.EnsureCreated();
        var config = new SqliteClientConfigStore(paths.ConfigDatabase);
        var drive = Path.Combine(_root, "SyncDrive");
        var fotos = Path.Combine(_root, "Fotos");
        foreach (var folder in new[] { drive, fotos })
        {
            _ = new LocalFileStore(folder); // legt den (versteckten) Arbeitsordner an
            await File.WriteAllTextAsync(Path.Combine(folder, "meins.txt"), "bleibt");
        }

        await config.SaveTaskAsync(new SyncTaskDefinition("t1", "c1", "s1", RelativePath.Root, drive), default);
        await config.SaveTaskAsync(new SyncTaskDefinition("t2", "c1", "s1", RelativePath.Parse("Fotos"), fotos, OnDemand: true), default);
        var cloud = new RecordingCloudFolders();
        var steps = new RecordingSteps();
        var log = new List<string>();

        await new ClientUninstaller(paths, config, cloud, steps, log.Add).RunAsync(default);

        Assert.Equal(["t1", "t2"], cloud.Detached.Order());
        Assert.Equal(["sync-roots", "menu", "autostart", "navigation", "key"], steps.Done);
        Assert.False(Directory.Exists(paths.Root));
        Assert.False(Directory.Exists(Path.Combine(drive, LocalFileStore.InternalFolder)));
        Assert.False(Directory.Exists(Path.Combine(fotos, LocalFileStore.InternalFolder)));
        Assert.Equal("bleibt", await File.ReadAllTextAsync(Path.Combine(drive, "meins.txt")));
        Assert.Equal(["2 Einträge im Navigationsbereich entfernt"], log);
    }

    [Fact]
    public async Task A_failing_step_does_not_stop_the_others()
    {
        var paths = new ClientPaths(Path.Combine(_root, "profil"));
        paths.EnsureCreated();
        var steps = new RecordingSteps { FailMenu = true };
        var log = new List<string>();

        await new ClientUninstaller(paths, new SqliteClientConfigStore(paths.ConfigDatabase), null, steps, log.Add).RunAsync(default);

        Assert.Contains("Explorer-Menü entfernen: Registry gesperrt", log);
        Assert.Equal(["sync-roots", "autostart", "navigation", "key"], steps.Done);
        Assert.False(Directory.Exists(paths.Root));
    }

    private sealed class RecordingCloudFolders : ICloudFolders
    {
        public List<string> Detached { get; } = [];

        public Task<CloudFolder> AttachAsync(SyncTaskDefinition task, Hydrator hydrator, CancellationToken ct) => throw new NotSupportedException();

        public Task DetachAsync(SyncTaskDefinition task, CancellationToken ct)
        {
            Detached.Add(task.Id);
            return Task.CompletedTask;
        }

        public Task SetPinnedAsync(string localPath, bool keep, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingSteps : IUninstallSteps
    {
        public bool FailMenu { get; init; }

        public List<string> Done { get; } = [];

        public void RemoveExplorerMenu()
        {
            if (FailMenu)
            {
                throw new UnauthorizedAccessException("Registry gesperrt");
            }

            Done.Add("menu");
        }

        public void RemoveAutostart() => Done.Add("autostart");

        public Task UnregisterRemainingSyncRootsAsync(CancellationToken ct)
        {
            Done.Add("sync-roots");
            return Task.CompletedTask;
        }

        public int RemoveNavigationEntries()
        {
            Done.Add("navigation");
            return 2;
        }

        public void RemoveSettingsKey() => Done.Add("key");
    }
}
