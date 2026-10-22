using Microsoft.Data.Sqlite;
using SyncDrive.Client.App.Ports;
using SyncDrive.Infra.Fs;

namespace SyncDrive.Client.Daemon;

/// <summary>Was beim Deinstallieren außerhalb der Ablage des Clients aufzuräumen ist; unter Windows Registry und Shell.</summary>
public interface IUninstallSteps
{
    /// <summary>Explorer-Menü „SyncDrive“.</summary>
    void RemoveExplorerMenu();

    /// <summary>Autostart beim Anmelden.</summary>
    void RemoveAutostart();

    /// <summary>Sync-Roots dieses Benutzers, die zu keiner gespeicherten Aufgabe mehr gehören (z. B. früher gelöschte Aufgaben).</summary>
    Task UnregisterRemainingSyncRootsAsync(CancellationToken ct);

    /// <summary>Einträge „SyncDrive“ im Navigationsbereich des Explorers, auch verwaiste; liefert ihre Anzahl.</summary>
    int RemoveNavigationEntries();

    /// <summary><c>HKCU\Software\SyncDrive</c>.</summary>
    void RemoveSettingsKey();
}

/// <summary>
/// Deinstallation (<c>syncdrive-daemon --uninstall</c>, vom Installer aufgerufen, nicht bei Updates): meldet alle Sync-Ordner bei Windows ab
/// (Platzhalter ohne Inhalt verschwinden, alles andere wird wieder zu gewöhnlichen Dateien), löscht den Arbeitsordner <c>.syncdrive</c>
/// in jedem Sync-Ordner, entfernt Menü, Autostart und Registry-Einträge und zuletzt die ganze Ablage (Konfiguration, Anmeldedaten, Logs).
/// Die synchronisierten Dateien bleiben. Jeder Schritt läuft auch, wenn ein vorheriger scheitert.
/// </summary>
public sealed class ClientUninstaller(
    ClientPaths paths, IClientConfigStore config, ICloudFolders? cloudFolders, IUninstallSteps steps, Action<string> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        IReadOnlyList<App.Tasks.SyncTaskDefinition> tasks = [];
        await StepAsync("Aufgaben lesen", async () => tasks = await config.ListTasksAsync(ct));
        foreach (var task in tasks)
        {
            if (cloudFolders is not null)
            {
                await StepAsync($"{task.LocalPath} abmelden", () => cloudFolders.DetachAsync(task, ct));
            }

            await StepAsync($"{task.LocalPath} Arbeitsordner löschen", () => DeleteTree(Path.Combine(task.LocalPath, LocalFileStore.InternalFolder)));
        }

        await StepAsync("übrige Sync-Ordner abmelden", () => steps.UnregisterRemainingSyncRootsAsync(ct));
        await StepAsync("Explorer-Menü entfernen", steps.RemoveExplorerMenu);
        await StepAsync("Autostart entfernen", steps.RemoveAutostart);
        await StepAsync("Navigationsbereich aufräumen", () => log($"{steps.RemoveNavigationEntries()} Einträge im Navigationsbereich entfernt"));
        await StepAsync("Registry-Schlüssel entfernen", steps.RemoveSettingsKey);
        SqliteConnection.ClearAllPools(); // offene Verbindungen halten client.db fest
        await StepAsync("Ablage löschen", () => DeleteTree(paths.Root));
    }

    private async Task StepAsync(string what, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"{what}: {e.Message}");
        }
    }

    private Task StepAsync(string what, Action step) => StepAsync(what, () =>
    {
        step();
        return Task.CompletedTask;
    });

    /// <summary>Auch schreibgeschützte und versteckte Einträge (der Arbeitsordner ist versteckt).</summary>
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            entry.Attributes = FileAttributes.Normal;
        }

        new DirectoryInfo(path).Attributes = FileAttributes.Directory;
        Directory.Delete(path, recursive: true);
    }
}
