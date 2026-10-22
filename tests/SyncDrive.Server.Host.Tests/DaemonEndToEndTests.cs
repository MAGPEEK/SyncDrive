using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Client.Daemon;
using SyncDrive.Contracts;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.Http;
using SyncDrive.Infra.Ipc;
using SyncDrive.Infra.Sqlite.Client;

namespace SyncDrive.Server.Host.Tests;

/// <summary>
/// Zwei vollständige Daemons (Konfigurationsdatenbank, Geheimnisse, Aufgabendatenbank, Watcher, HTTP) gegen den echten Server-Host;
/// die Oberfläche fragt über die echte Pipe. Nur die TCP-Verbindung ist durch den In-Process-Server ersetzt.
/// </summary>
public sealed class DaemonEndToEndTests(HostFactory factory) : IClassFixture<HostFactory>, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _daemons = [];
    private readonly List<string> _directories = [];

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_daemons.Select(d => d.ContinueWith(_ => { }, TaskScheduler.Default))).WaitAsync(TimeSpan.FromSeconds(15));
        foreach (var dir in _directories)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        _stop.Dispose();
    }

    private HttpRemoteApi Remote(HttpRemoteOptions options)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost/api/v1/") });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.DeviceToken);
        return new HttpRemoteApi(http, options.SpaceId);
    }

    /// <summary>Richtet einen Daemon so ein, wie es der Assistent täte: Gerät anmelden, Verbindung und Aufgabe speichern, Token in den Geheimnisspeicher.</summary>
    private async Task<(string Folder, string Pipe)> StartDaemonAsync(string name, string spaceId, RelativePath remoteFolder)
    {
        var root = Path.Combine(Path.GetTempPath(), $"syncdrive-daemon-{name}-{Guid.NewGuid():N}");
        _directories.Add(root);
        var paths = new ClientPaths(Path.Combine(root, "profil"));
        paths.EnsureCreated();
        var folder = Path.Combine(root, "SyncDrive");
        Directory.CreateDirectory(folder);

        var login = await factory.LoginAsync();
        var config = new SqliteClientConfigStore(paths.ConfigDatabase);
        await config.SaveConnectionAsync(new ConnectionInfo("c1", "Test-NAS", new Uri("http://localhost/"), HostFactory.AdminName, login.Registration.DeviceId, null), default);
        await config.SaveTaskAsync(new SyncTaskDefinition("t1", "c1", spaceId, remoteFolder, folder), default);
        var credentials = new MemoryCredentialStore();
        await credentials.SaveAsync(CredentialKeys.DeviceToken("c1"), login.Registration.Token, default);

        var pipe = "syncdrive-e2e-" + Guid.NewGuid().ToString("N");
        var options = new SupervisorOptions(
            new SyncRunnerOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), 2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromSeconds(1));
        var host = new DaemonHost(paths, config, credentials, name, pipe, options, Remote);
        _daemons.Add(host.RunAsync(_stop.Token));
        return (folder, pipe);
    }

    [Fact]
    public async Task Two_daemons_sync_a_server_folder_and_report_their_status_over_the_pipe()
    {
        var admin = await factory.LoginAsync();
        var spaceId = await admin.FirstSpaceIdAsync();
        var remoteFolder = RelativePath.Parse("daemon-" + Guid.NewGuid().ToString("N")[..8]);
        var created = await Remote(new HttpRemoteOptions(new Uri("http://localhost/"), admin.Registration.Token, spaceId))
            .CommitAsync(new CommitRequest(CommitOps.Mkdir, remoteFolder.Value, 0), null, default);
        Assert.True(created.IsSuccess, created.Error?.Message);

        var (folderA, pipeA) = await StartDaemonAsync("A", spaceId, remoteFolder);
        var (folderB, pipeB) = await StartDaemonAsync("B", spaceId, remoteFolder);
        await using var uiA = await IpcClient.ConnectAsync(pipeA, TimeSpan.FromSeconds(10), default);
        await using var uiB = await IpcClient.ConnectAsync(pipeB, TimeSpan.FromSeconds(10), default);
        await EventuallyAsync(async () => (await uiA.GetStatusAsync(default)).Tray == TrayState.UpToDate
            && (await uiB.GetStatusAsync(default)).Tray == TrayState.UpToDate, "beide Daemons verbunden");

        await File.WriteAllTextAsync(Path.Combine(folderA, "bericht.txt"), "vom Daemon A");

        var arrived = Path.Combine(folderB, "bericht.txt");
        await EventuallyAsync(() => Task.FromResult(File.Exists(arrived) && File.ReadAllText(arrived) == "vom Daemon A"), "Datei erreicht Daemon B");
        await EventuallyAsync(
            async () => (await uiB.GetRecentActivityAsync(10, default)).Any(a => a.Entry.Path.Value == "bericht.txt" && a.Entry.Action == ActivityAction.Download),
            "B meldet den Download im Verlauf");
    }

    [Fact]
    public async Task A_task_without_stored_token_is_listed_as_disabled_with_a_sign_in_hint()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncdrive-daemon-x-" + Guid.NewGuid().ToString("N"));
        _directories.Add(root);
        var paths = new ClientPaths(root);
        paths.EnsureCreated();
        var config = new SqliteClientConfigStore(paths.ConfigDatabase);
        await config.SaveConnectionAsync(new ConnectionInfo("c1", "Test-NAS", new Uri("http://localhost/"), "admin", "d1", null), default);
        await config.SaveTaskAsync(new SyncTaskDefinition("t1", "c1", "s1", RelativePath.Root, root), default);
        var pipe = "syncdrive-e2e-" + Guid.NewGuid().ToString("N");
        _daemons.Add(new DaemonHost(paths, config, new MemoryCredentialStore(), "X", pipe, SupervisorOptions.Default, Remote).RunAsync(_stop.Token));

        await using var ui = await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(10), default);
        OverallStatus? status = null;
        await EventuallyAsync(async () => (status = await ui.GetStatusAsync(default)).Tasks.Count == 1, "Aufgabe gelistet");

        Assert.Equal(TaskState.Disabled, status!.Tasks[0].State);
        Assert.Contains("Test-NAS", status.Tasks[0].LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wizard_flow_over_the_pipe_signs_in_browses_creates_a_task_and_it_syncs_at_once()
    {
        var admin = await factory.LoginAsync();
        var spaceId = await admin.FirstSpaceIdAsync();
        var adminRemote = Remote(new HttpRemoteOptions(new Uri("http://localhost/"), admin.Registration.Token, spaceId));
        var remoteFolder = "assistent-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True((await adminRemote.CommitAsync(new CommitRequest(CommitOps.Mkdir, remoteFolder, 0), null, default)).IsSuccess);

        var root = Path.Combine(Path.GetTempPath(), "syncdrive-wizard-" + Guid.NewGuid().ToString("N"));
        _directories.Add(root);
        var paths = new ClientPaths(Path.Combine(root, "profil"));
        paths.EnsureCreated();
        var pipe = "syncdrive-e2e-" + Guid.NewGuid().ToString("N");
        var host = new DaemonHost(
            paths, new SqliteClientConfigStore(paths.ConfigDatabase), new MemoryCredentialStore(), "Laptop", pipe,
            new SupervisorOptions(
                new SyncRunnerOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), 2, TimeSpan.FromSeconds(2)),
                TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)),
            Remote,
            new HttpServerAccess(_ => factory.Server.CreateHandler()),
            environment: canWrite => new TaskEnvironment(_ => true, root, [], canWrite));
        _daemons.Add(host.RunAsync(_stop.Token));
        await using var ui = await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(10), default);

        var wrong = await ui.SignInAsync(new SignInRequest("http://localhost", HostFactory.AdminName, "falsch"), default);
        Assert.Equal(SetupErrors.SignInFailed, wrong.ErrorCode);
        var signedIn = await ui.SignInAsync(new SignInRequest("http://localhost", HostFactory.AdminName, HostFactory.AdminPassword), default);
        Assert.True(signedIn.Succeeded, signedIn.Message);

        var space = (await ui.BrowseAsync(signedIn.ConnectionId!, null, string.Empty, default)).First(s => s.SpaceId == spaceId);
        var folder = (await ui.BrowseAsync(signedIn.ConnectionId!, space.SpaceId, string.Empty, default)).Single(f => f.Name == remoteFolder);
        var local = Path.Combine(root, "SyncDrive");
        var created = await ui.CreateTaskAsync(new NewTaskRequest(signedIn.ConnectionId!, space.SpaceId, folder.Path, local), default);
        Assert.True(created.Succeeded, string.Join(",", created.Violations));

        var nested = await ui.CreateTaskAsync(new NewTaskRequest(signedIn.ConnectionId!, space.SpaceId, folder.Path, Path.Combine(local, "x")), default);
        Assert.Equal([TaskRuleViolation.LocalFolderOverlapsTask], nested.Violations);

        await EventuallyAsync(async () => (await ui.GetStatusAsync(default)).Tray == TrayState.UpToDate, "neue Aufgabe läuft");
        await File.WriteAllTextAsync(Path.Combine(local, "notiz.txt"), "aus dem Assistenten");
        await EventuallyAsync(
            async () => (await adminRemote.GetNodeAsync(RelativePath.Parse(remoteFolder + "/notiz.txt"), default)).Value is { State: not null },
            "Datei liegt im gewählten Serverordner");

        var task = Assert.Single(await ui.GetTasksAsync(default));
        Assert.Equal((remoteFolder, local), (task.RemotePath, task.LocalPath));
        await ui.DeleteTaskAsync(task.TaskId, default);
        Assert.Empty(await ui.GetTasksAsync(default));
        Assert.Empty((await ui.GetStatusAsync(default)).Tasks);
    }

    [Fact]
    public async Task A_shared_space_reaches_the_new_member_as_notification_with_red_dot_and_usage_is_reported()
    {
        var admin = await factory.LoginAsync();
        var spaceId = await admin.FirstSpaceIdAsync();
        var benName = await factory.CreateUserAsync("ben-" + Guid.NewGuid().ToString("N")[..8]);

        var root = Path.Combine(Path.GetTempPath(), "syncdrive-notes-" + Guid.NewGuid().ToString("N"));
        _directories.Add(root);
        var paths = new ClientPaths(root);
        paths.EnsureCreated();
        var pipe = "syncdrive-e2e-" + Guid.NewGuid().ToString("N");
        _daemons.Add(new DaemonHost(
            paths, new SqliteClientConfigStore(paths.ConfigDatabase), new MemoryCredentialStore(), "Ben-PC", pipe, SupervisorOptions.Default, Remote,
            new HttpServerAccess(_ => factory.Server.CreateHandler()),
            environment: canWrite => new TaskEnvironment(_ => true, root, [], canWrite),
            notificationInterval: TimeSpan.FromMilliseconds(200)).RunAsync(_stop.Token));
        await using var ui = await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(10), default);
        var signedIn = await ui.SignInAsync(new SignInRequest("http://localhost", benName, "another-long-password"), default);
        Assert.True(signedIn.Succeeded, signedIn.Message);

        var shared = await admin.Http.PostAsJsonAsync($"/api/v1/spaces/{spaceId}/members", new AddMemberRequest(benName, "editor"));
        Assert.True(shared.IsSuccessStatusCode);

        await EventuallyAsync(async () => (await ui.GetStatusAsync(default)).HasNotifications, "roter Punkt am Tray");
        var note = Assert.Single(await ui.GetNotificationsAsync(default));
        Assert.Equal(("shared", HostFactory.AdminName, signedIn.ConnectionId), (note.Type, note.Actor, note.ConnectionId));

        await ui.MarkNotificationsReadAsync(default);
        Assert.False((await ui.GetStatusAsync(default)).HasNotifications);

        var created = await ui.CreateTaskAsync(new NewTaskRequest(signedIn.ConnectionId!, spaceId, string.Empty, Path.Combine(root, "Geteilt")), default);
        Assert.True(created.Succeeded);
        var usage = Assert.Single(await ui.GetUsageAsync(default));
        Assert.Equal(created.TaskId, usage.TaskId);
        Assert.NotNull(usage.Bytes);
    }

    [Fact]
    public async Task Explorer_actions_report_status_save_and_restore_versions_lock_and_exclude_folders()
    {
        var admin = await factory.LoginAsync();
        var spaceId = await admin.FirstSpaceIdAsync();
        var adminRemote = Remote(new HttpRemoteOptions(new Uri("http://localhost/"), admin.Registration.Token, spaceId));
        var remoteFolder = "explorer-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True((await adminRemote.CommitAsync(new CommitRequest(CommitOps.Mkdir, remoteFolder, 0), null, default)).IsSuccess);

        var root = Path.Combine(Path.GetTempPath(), "syncdrive-explorer-" + Guid.NewGuid().ToString("N"));
        _directories.Add(root);
        var paths = new ClientPaths(Path.Combine(root, "profil"));
        paths.EnsureCreated();
        var pipe = "syncdrive-e2e-" + Guid.NewGuid().ToString("N");
        _daemons.Add(new DaemonHost(
            paths, new SqliteClientConfigStore(paths.ConfigDatabase), new MemoryCredentialStore(), "Laptop", pipe,
            new SupervisorOptions(
                new SyncRunnerOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), 2, TimeSpan.FromSeconds(2)),
                TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)),
            Remote, new HttpServerAccess(_ => factory.Server.CreateHandler()),
            environment: canWrite => new TaskEnvironment(_ => true, root, [], canWrite)).RunAsync(_stop.Token));
        await using var ui = await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(10), default);
        var connection = (await ui.SignInAsync(new SignInRequest("http://localhost", HostFactory.AdminName, HostFactory.AdminPassword), default)).ConnectionId!;
        var local = Path.Combine(root, "SyncDrive");
        Assert.True((await ui.CreateTaskAsync(new NewTaskRequest(connection, spaceId, remoteFolder, local), default)).Succeeded);
        await EventuallyAsync(async () => (await ui.GetStatusAsync(default)).Tray == TrayState.UpToDate, "Aufgabe läuft");

        var file = Path.Combine(local, "bericht.txt");
        var remoteFile = RelativePath.Parse(remoteFolder + "/bericht.txt");
        await File.WriteAllTextAsync(file, "erste Fassung");
        await EventuallyAsync(async () => (await adminRemote.GetNodeAsync(remoteFile, default)).Value is { State.Size: 13 }, "Fassung 1 hochgeladen");
        await File.WriteAllTextAsync(file, "zweite, längere Fassung");
        await EventuallyAsync(async () => (await adminRemote.GetNodeAsync(remoteFile, default)).Value is { State.Size: > 13 }, "Fassung 2 hochgeladen");

        // Status: Der Server hat die Fassung schon, bevor der Client seine Basis fortschreibt; bis dahin heißt es zu Recht „Syncing“.
        await EventuallyAsync(async () => (await ui.GetPathInfoAsync(file, default))!.Status == PathStatus.UpToDate, "Datei aktuell");
        await File.WriteAllTextAsync(Path.Combine(local, "~$bericht.docx"), "Sperrdatei");
        Assert.Equal(PathStatus.Excluded, (await ui.GetPathInfoAsync(Path.Combine(local, "~$bericht.docx"), default))!.Status);
        Assert.Null(await ui.GetPathInfoAsync(Path.Combine(root, "profil"), default));

        // Frühere Version speichern und wiederherstellen
        var versions = await ui.ListVersionsAsync(file, default);
        var first = Assert.Single(versions, v => v.Size == 13);
        Assert.Contains(versions, v => v.IsCurrent);
        var copy = Path.Combine(root, "bericht (alt).txt");
        Assert.True((await ui.SaveVersionAsAsync(file, first.Revision, copy, default)).Succeeded);
        Assert.Equal("erste Fassung", await File.ReadAllTextAsync(copy));
        Assert.True((await ui.RestoreVersionAsync(file, first.Revision, default)).Succeeded);
        await EventuallyAsync(() => Task.FromResult(File.ReadAllText(file) == "erste Fassung"), "wiederhergestellte Version kommt lokal an");

        // Sperren
        Assert.True((await ui.LockAsync(file, LockAction.Lock, default)).Succeeded);
        Assert.True((await ui.LockAsync(file, LockAction.Unlock, default)).Succeeded);
        Assert.False((await ui.LockAsync(file, LockAction.RequestUnlock, default)).Succeeded); // nicht fremd gesperrt

        // Ordner ab- und wieder anwählen
        Directory.CreateDirectory(Path.Combine(local, "Archiv"));
        Assert.True((await ui.SetFolderSyncedAsync(Path.Combine(local, "Archiv"), synced: false, default)).Succeeded);
        Assert.Equal(PathStatus.Excluded, (await ui.GetPathInfoAsync(Path.Combine(local, "Archiv"), default))!.Status);
        Assert.Equal(ShellErrors.NotPossible, (await ui.SetFolderSyncedAsync(local, synced: false, default)).ErrorCode);
        Assert.True((await ui.SetFolderSyncedAsync(Path.Combine(local, "Archiv"), synced: true, default)).Succeeded);
        Assert.NotEqual(PathStatus.Excluded, (await ui.GetPathInfoAsync(Path.Combine(local, "Archiv"), default))!.Status);
    }

    [Fact]
    public void The_proxy_setting_is_applied_to_the_http_handler()
    {
        using var manual = new SocketsHttpHandler();
        using var none = new SocketsHttpHandler();
        using var auto = new SocketsHttpHandler();

        Proxies.Apply(manual, new ProxyConfig(Client.App.Settings.ProxyMode.Manual, "proxy.lan", 3128, "bob", "geheim"));
        Proxies.Apply(none, ProxyConfig.None);
        Proxies.Apply(auto, new ProxyConfig(Client.App.Settings.ProxyMode.Auto));

        var proxy = Assert.IsType<System.Net.WebProxy>(manual.Proxy);
        Assert.Equal(new Uri("http://proxy.lan:3128/"), proxy.Address);
        Assert.Equal("bob", ((System.Net.NetworkCredential)proxy.Credentials!).UserName);
        Assert.Equal((true, false, true), (manual.UseProxy, none.UseProxy, auto.UseProxy));
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Zeitüberschreitung: " + what);
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
