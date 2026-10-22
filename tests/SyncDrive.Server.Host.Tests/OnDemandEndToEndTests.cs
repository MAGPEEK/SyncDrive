using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Client.Daemon;
using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.CloudFiles;
using SyncDrive.Infra.Fs;
using SyncDrive.Infra.Http;
using SyncDrive.Infra.Ipc;
using SyncDrive.Infra.Sqlite.Client;

namespace SyncDrive.Server.Host.Tests;

/// <summary>
/// On-Demand von Anfang bis Ende mit echter Cloud-Files-API: Assistent über die Pipe legt eine On-Demand-Aufgabe an, Serverdateien kommen als
/// Platzhalter, ein fremder Prozess öffnet einen und bekommt den Inhalt, neue lokale Dateien gehen hoch, Löschen der Aufgabe räumt auf.
/// Ändert die Sync-Root-Anmeldung des Benutzers (und der Shell), daher nur mit <c>SYNCDRIVE_CFAPI_TESTS=1</c> unter Windows.
/// </summary>
public sealed class OnDemandEndToEndTests(HostFactory factory) : IClassFixture<HostFactory>
{
    [CloudFilesFact]
    public async Task An_on_demand_task_shows_server_files_as_placeholders_fetches_on_open_and_cleans_up_on_delete()
    {
        var admin = await factory.LoginAsync();
        var spaceId = await admin.FirstSpaceIdAsync();
        var adminRemote = Remote(new HttpRemoteOptions(new Uri("http://localhost/"), admin.Registration.Token, spaceId));
        var remoteFolder = "ondemand-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True((await adminRemote.CommitAsync(new CommitRequest(CommitOps.Mkdir, remoteFolder, 0), null, default)).IsSuccess);
        await PutAsync(adminRemote, remoteFolder + "/bericht.txt", "Bericht vom Server");
        await PutAsync(adminRemote, remoteFolder + "/bleibt-online.txt", "nie geöffnet");

        var root = Path.Combine(Path.GetTempPath(), "syncdrive-ondemand-" + Guid.NewGuid().ToString("N"));
        var paths = new ClientPaths(Path.Combine(root, "profil"));
        paths.EnsureCreated();
        var local = Path.Combine(root, "SyncDrive");
        var log = new ConcurrentQueue<string>();
        var cloud = new WindowsCloudFolders(
            "0.0.0-test", log.Enqueue, Path.Combine(AppContext.BaseDirectory, "cloudreg", "syncdrive-cloudreg.exe"),
            Path.Combine(Environment.SystemDirectory, "imageres.dll") + ",0");
        var pipe = "syncdrive-e2e-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var host = new DaemonHost(
            paths, new SqliteClientConfigStore(paths.ConfigDatabase), new MemoryCredentialStore(), "Laptop", pipe,
            new SupervisorOptions(
                new SyncRunnerOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), 2, TimeSpan.FromSeconds(2)),
                TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)),
            Remote,
            new HttpServerAccess(_ => factory.Server.CreateHandler()),
            environment: canWrite => new TaskEnvironment(_ => true, root, [], canWrite),
            cloudFolders: cloud,
            log: log.Enqueue);
        var daemon = host.RunAsync(stop.Token);
        try
        {
            await using var ui = await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(10), default);
            var signedIn = await ui.SignInAsync(new SignInRequest("http://localhost", HostFactory.AdminName, HostFactory.AdminPassword), default);
            Assert.True(signedIn.Succeeded, signedIn.Message);
            var created = await ui.CreateTaskAsync(
                new NewTaskRequest(signedIn.ConnectionId!, spaceId, remoteFolder, local, OnDemand: true), default);
            Assert.True(created.Succeeded, string.Join(",", created.Violations) + created.Message);

            // 1. Serverdateien erscheinen als Platzhalter: richtige Größe, kein Inhalt auf der Platte.
            var store = new LocalFileStore(local);
            await EventuallyAsync(
                async () => await store.StatAsync(RelativePath.Parse("bleibt-online.txt"), default) is { IsCloudOnly: true },
                "Platzhalter angelegt", log);
            var report = await store.StatAsync(RelativePath.Parse("bericht.txt"), default);
            Assert.True(report is { IsCloudOnly: true, Size: 18 }, $"{report}");

            // 2. Ein anderes Programm öffnet ihn: der Inhalt kommt vom Server, danach ist die Datei lokal; der Server bleibt unverändert.
            var revision = (await adminRemote.GetLatestRevisionAsync(default)).Value;
            Assert.Equal("Bericht vom Server", await ReadInOtherProcessAsync(Path.Combine(local, "bericht.txt")));
            Assert.False((await store.StatAsync(RelativePath.Parse("bericht.txt"), default))!.IsCloudOnly);

            // 3. Neue lokale Datei geht hoch wie gewohnt.
            await File.WriteAllTextAsync(Path.Combine(local, "neu.txt"), "vom Laptop");
            await EventuallyAsync(
                async () => (await adminRemote.GetNodeAsync(RelativePath.Parse(remoteFolder + "/neu.txt"), default)).Value is { State: not null },
                "neue Datei auf dem Server", log);
            await EventuallyAsync(async () => (await ui.GetStatusAsync(default)).Tray == TrayState.UpToDate, "abgeglichen", log);
            var changes = (await adminRemote.GetChangesAsync(revision, 100, null, default)).Value!;
            Assert.All(changes.Changes, c => Assert.EndsWith("neu.txt", c.Path, StringComparison.Ordinal)); // Öffnen hat nichts hochgeladen

            // 4. Aufgabe löschen: Platzhalter ohne Inhalt verschwinden, Geöffnetes und Eigenes bleibt als gewöhnliche Datei.
            var task = Assert.Single(await ui.GetTasksAsync(default));
            await ui.DeleteTaskAsync(task.TaskId, default);
            Assert.Equal(
                (false, "Bericht vom Server", "vom Laptop", false),
                (File.Exists(Path.Combine(local, "bleibt-online.txt")),
                 await File.ReadAllTextAsync(Path.Combine(local, "bericht.txt")),
                 await File.ReadAllTextAsync(Path.Combine(local, "neu.txt")),
                 File.GetAttributes(Path.Combine(local, "bericht.txt")).HasFlag(FileAttributes.ReparsePoint)));
            Assert.Equal(string.Empty, string.Join(" | ", log.Where(l => !l.StartsWith("Daemon", StringComparison.Ordinal))));
        }
        finally
        {
            await stop.CancelAsync();
            await daemon.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(15));
            Directory.Delete(root, recursive: true);
        }
    }

    private HttpRemoteApi Remote(HttpRemoteOptions options)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost/api/v1/") });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.DeviceToken);
        return new HttpRemoteApi(http, options.SpaceId);
    }

    /// <summary>Legt eine kleine Datei (ein Block) direkt auf dem Server an, wie es ein anderes Gerät täte.</summary>
    private static async Task PutAsync(HttpRemoteApi remote, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = ContentHash.FromBytes(SHA256.HashData(bytes));
        Assert.True((await remote.UploadBlockAsync(hash, new MemoryStream(bytes), default)).IsSuccess);
        var put = await remote.CommitAsync(
            new CommitRequest(CommitOps.Put, path, 0, Blocks: [hash.Value], Size: bytes.Length, MTime: DateTimeOffset.UtcNow.AddMinutes(-5), FileHash: hash.Value),
            null, default);
        Assert.True(put.IsSuccess, put.Error?.Message);
    }

    /// <summary>Wie ein gewöhnliches Programm: der Anbieterprozess selbst darf seine Platzhalter nicht laden.</summary>
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

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what, IEnumerable<string> log)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Zeitüberschreitung: {what}; Protokoll: {string.Join(" | ", log)}");
            await Task.Delay(100);
        }
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly ConcurrentDictionary<string, string> _secrets = new();

        public Task SaveAsync(string key, string secret, CancellationToken ct)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult(_secrets.GetValueOrDefault(key));

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            _secrets.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Nur mit ausdrücklicher Freigabe: registriert Sync-Roots im echten System.</summary>
public sealed class CloudFilesFactAttribute : FactAttribute
{
    public CloudFilesFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SYNCDRIVE_CFAPI_TESTS") != "1")
        {
            Skip = "Nur unter Windows mit SYNCDRIVE_CFAPI_TESTS=1 (ändert die Sync-Root-Registrierung des Benutzers).";
        }
    }
}
