using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Client.Tests.Scenarios;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.CloudFiles;
using SyncDrive.Infra.Fs;

namespace SyncDrive.Client.Tests;

/// <summary>
/// Echte Cloud-Files-API: registriert einen Temp-Ordner als Sync-Root, legt einen Platzhalter an, liest ihn (Abruf über den Hydrator),
/// setzt den Status und meldet alles wieder ab. Ändert das System (Registrierung beim Benutzer), daher nur mit
/// <c>SYNCDRIVE_CFAPI_TESTS=1</c> unter Windows.
/// </summary>
public sealed class CloudFilesTests
{
    [CloudFilesFact]
    public async Task A_placeholder_is_fetched_on_first_read_and_the_folder_is_released_afterwards()
    {
        using var cluster = new Cluster();
        var a = cluster.AddClient("A");
        a.Write("docs/a.txt", "Inhalt vom Server");
        await a.SyncOkAsync();
        var node = (await a.Remote.GetNodeAsync(RelativePath.Parse("docs/a.txt"), default)).Value!;

        var root = Path.Combine(Path.GetTempPath(), "syncdrive-cfapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var task = new SyncTaskDefinition("cfapi-test", "c", Cluster.Space, RelativePath.Root, root, OnDemand: true);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var folders = Folders(log.Enqueue);
        try
        {
            var folder = await folders.AttachAsync(task, new Hydrator(a.Remote), default);
            await using (folder.Connection)
            {
                await folder.Placeholders.CreateAsync(RelativePath.Parse("docs/a.txt"), node.State!.Size, node.State.MTime, node.State.Hash!.Value, default);
                var local = new LocalFileStore(root);

                var file = Path.Combine(root, "docs", "a.txt");
                var attributesBefore = File.GetAttributes(file);
                var before = await local.StatAsync(RelativePath.Parse("docs/a.txt"), default);
                var content = await ReadInOtherProcessAsync(file);
                var after = await local.StatAsync(RelativePath.Parse("docs/a.txt"), default);
                await folder.Placeholders.SetInSyncAsync(RelativePath.Parse("docs"), true, default);

                // Alles in einem Vergleich: ein Lauf zeigt jedes Ergebnis (und die Attribute zur Diagnose).
                Assert.Equal(
                    ("cloud-only 17", "Inhalt vom Server", "lokal 17", ""),
                    (Describe(before, attributesBefore), content, Describe(after, File.GetAttributes(file)), string.Join(" | ", log)));
            }
        }
        finally
        {
            await folders.DetachAsync(task, default);
            Directory.Delete(root, recursive: true);
        }
    }

    [CloudFilesFact]
    public async Task Keep_fetches_free_up_releases_and_windows_leaves_both_out_of_its_own_menu()
    {
        using var cluster = new Cluster();
        var a = cluster.AddClient("A");
        a.Write("docs/a.txt", "Inhalt vom Server");
        await a.SyncOkAsync();
        var node = (await a.Remote.GetNodeAsync(RelativePath.Parse("docs/a.txt"), default)).Value!;

        var root = Path.Combine(Path.GetTempPath(), "syncdrive-cfapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var task = new SyncTaskDefinition("cfapi-pin", "c", Cluster.Space, RelativePath.Root, root, OnDemand: true);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var folders = Folders(log.Enqueue);
        try
        {
            var folder = await folders.AttachAsync(task, new Hydrator(a.Remote), default);
            await using (folder.Connection)
            {
                await folder.Placeholders.CreateAsync(RelativePath.Parse("docs/a.txt"), node.State!.Size, node.State.MTime, node.State.Hash!.Value, default);
                var file = Path.Combine(root, "docs", "a.txt");
                var menu = OperatingSystem.IsWindows() ? ContextMenuOf(file) : []; // das Attribut lässt den Test ohnehin nur unter Windows laufen

                await folders.SetPinnedAsync(Path.Combine(root, "docs"), keep: true, default); // Ordner: wirkt auf den Inhalt
                var kept = await WaitForAsync(() => !IsCloudOnly(file));
                await folder.Placeholders.SetInSyncAsync(RelativePath.Parse("docs/a.txt"), true, default);
                await folders.SetPinnedAsync(file, keep: false, default);
                var freed = await WaitForAsync(() => IsCloudOnly(file));

                Assert.Equal(
                    (true, true, "", ""),
                    (kept, freed, string.Join(" | ", menu.Where(v => v.Contains("Gerät", StringComparison.Ordinal)
                        || v.Contains("device", StringComparison.OrdinalIgnoreCase) || v.Contains("freigeben", StringComparison.Ordinal)
                        || v.Contains("Free up", StringComparison.OrdinalIgnoreCase))), string.Join(" | ", log)));
            }
        }
        finally
        {
            await folders.DetachAsync(task, default);
            Directory.Delete(root, recursive: true);
        }
    }

    [CloudFilesFact]
    public async Task The_provider_process_itself_never_fetches_a_placeholder_implicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncdrive-cfapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var task = new SyncTaskDefinition("cfapi-self", "c", Cluster.Space, RelativePath.Root, root, OnDemand: true);
        var folders = Folders(null);
        using var cluster = new Cluster();
        try
        {
            var folder = await folders.AttachAsync(task, new Hydrator(cluster.AddClient("A").Remote), default);
            await using (folder.Connection)
            {
                await folder.Placeholders.CreateAsync(RelativePath.Parse("a.txt"), 5, DateTimeOffset.UtcNow, Core.Model.ContentHash.FromBytes(new byte[32]), default);

                // Der Daemon darf seine Platzhalter nie selbst (versehentlich) laden; Windows weist das ab.
                await Assert.ThrowsAnyAsync<IOException>(() => File.ReadAllTextAsync(Path.Combine(root, "a.txt")));
            }
        }
        finally
        {
            await folders.DetachAsync(task, default);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Sichtprüfung im Explorer (nur mit <c>SYNCDRIVE_CFAPI_SHOWCASE=1</c>): ein Sync-Ordner mit je einer abgeglichenen, einer nur online
    /// vorhandenen und einer nicht abgeglichenen Datei, verbunden, bis die Datei <c>&lt;Ordner&gt;.stop</c> erscheint (höchstens 10 Minuten).
    /// </summary>
    [CloudFilesFact(Showcase = true)]
    public async Task Showcase_for_a_look_in_explorer()
    {
        using var cluster = new Cluster();
        var a = cluster.AddClient("A");
        a.Write("nur-online.txt", "Dieser Inhalt kommt erst beim Öffnen vom Server.");
        await a.SyncOkAsync();
        var node = (await a.Remote.GetNodeAsync(RelativePath.Parse("nur-online.txt"), default)).Value!;

        var root = Path.Combine(Path.GetTempPath(), "SyncDrive-Sichtpruefung");
        Directory.CreateDirectory(root);
        var task = new SyncTaskDefinition("cfapi-showcase", "c", Cluster.Space, RelativePath.Root, root, OnDemand: true);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var folders = Folders(log.Enqueue);
        try
        {
            var folder = await folders.AttachAsync(task, new Hydrator(a.Remote), default);
            await using (folder.Connection)
            {
                var p = folder.Placeholders;
                await p.CreateAsync(RelativePath.Parse("nur-online.txt"), node.State!.Size, node.State.MTime, node.State.Hash!.Value, default);
                await File.WriteAllTextAsync(Path.Combine(root, "abgeglichen.txt"), "lokal und abgeglichen");
                await p.SetInSyncAsync(RelativePath.Parse("abgeglichen.txt"), true, default);
                await File.WriteAllTextAsync(Path.Combine(root, "nicht-abgeglichen.txt"), "wartet auf den Abgleich");
                await p.SetInSyncAsync(RelativePath.Parse("nicht-abgeglichen.txt"), true, default);
                await p.SetInSyncAsync(RelativePath.Parse("nicht-abgeglichen.txt"), false, default);
                using (System.Diagnostics.Process.Start("explorer.exe", $"\"{root}\""))
                {
                }

                var stop = root + ".stop";
                for (var waited = 0; waited < 600 && !File.Exists(stop); waited++)
                {
                    await Task.Delay(1000);
                }

                File.Delete(stop);
            }
        }
        finally
        {
            await folders.DetachAsync(task, default);
            Directory.Delete(root, recursive: true);
        }

        Assert.Equal(string.Empty, string.Join(" | ", log));
    }

    /// <summary>Wie im Daemon: mit Shell-Anmeldung über das Hilfsprogramm (liegt durch CloudRegistration.targets im Testordner).</summary>
    private static WindowsCloudFolders Folders(Action<string>? log) => new(
        "0.0.0-test", log, Path.Combine(AppContext.BaseDirectory, "cloudreg", "syncdrive-cloudreg.exe"),
        Path.Combine(Environment.SystemDirectory, "imageres.dll") + ",0");

    /// <summary>Einträge des Explorer-Kontextmenüs einer Datei (Shell.Application), ohne Zugriffstasten-Zeichen.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> ContextMenuOf(string path)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        var item = shell.NameSpace(Path.GetDirectoryName(path)).ParseName(Path.GetFileName(path));
        var names = new List<string>();
        foreach (var verb in item.Verbs())
        {
            names.Add(((string)verb.Name).Replace("&", string.Empty, StringComparison.Ordinal));
        }

        return names;
    }

    /// <summary>Nur online: Windows holt den Inhalt erst beim Zugriff (RECALL_ON_DATA_ACCESS, RECALL_ON_OPEN oder Offline).</summary>
    private static bool IsCloudOnly(string path) =>
        ((int)File.GetAttributes(path) & (0x00400000 | 0x00040000 | (int)FileAttributes.Offline)) != 0;

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }

        return condition();
    }

    private static string Describe(LocalEntry? entry, FileAttributes attributes) => entry switch
    {
        null => $"übersprungen ({attributes})",
        { IsCloudOnly: true } => $"cloud-only {entry.Size}",
        _ => $"lokal {entry.Size}",
    };

    /// <summary>Wie ein gewöhnliches Programm: ein anderer Prozess liest, Windows fragt den Anbieter nach dem Inhalt.</summary>
    private static async Task<string> ReadInOtherProcessAsync(string path)
    {
        using var reader = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c type \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = reader.StandardOutput.ReadToEndAsync();
        var error = reader.StandardError.ReadToEndAsync();
        await reader.WaitForExitAsync();
        return (await error).Trim() is { Length: > 0 } message ? $"{await output}[Fehler: {message}]" : await output;
    }
}

/// <summary>Nur mit ausdrücklicher Freigabe: registriert Sync-Roots im echten System.</summary>
public sealed class CloudFilesFactAttribute : FactAttribute
{
    private bool _showcase;

    public CloudFilesFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SYNCDRIVE_CFAPI_TESTS") != "1")
        {
            Skip = "Nur unter Windows mit SYNCDRIVE_CFAPI_TESTS=1 (ändert die Sync-Root-Registrierung des Benutzers).";
        }
    }

    /// <summary>Vorführlauf zum Anschauen; zusätzlich <c>SYNCDRIVE_CFAPI_SHOWCASE=1</c>, damit er nie in einem normalen Testlauf wartet.</summary>
    public bool Showcase
    {
        get => _showcase;
        set
        {
            _showcase = value;
            if (value && Skip is null && Environment.GetEnvironmentVariable("SYNCDRIVE_CFAPI_SHOWCASE") != "1")
            {
                Skip = "Vorführlauf nur mit SYNCDRIVE_CFAPI_SHOWCASE=1.";
            }
        }
    }
}
