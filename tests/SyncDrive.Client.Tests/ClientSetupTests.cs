using System.Collections.Concurrent;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Contracts;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;
using SyncDrive.Infra.Sqlite.Client;

namespace SyncDrive.Client.Tests;

/// <summary>Einrichtung im Daemon: Anmeldung mit Zertifikatsrückfrage, Ordnerbaum, Aufgabenregeln, Anlegen und Entfernen.</summary>
public sealed class ClientSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncdrive-setup-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteClientConfigStore _config;
    private readonly Secrets _secrets = new();
    private readonly FakeServer _server = new();
    private readonly Lifecycle _lifecycle = new();
    private readonly ClientSetup _setup;

    public ClientSetupTests()
    {
        Directory.CreateDirectory(_root);
        _config = new SqliteClientConfigStore(Path.Combine(_root, "client.db"));
        _setup = new ClientSetup(_config, _secrets, _server, _lifecycle, canWrite => new TaskEnvironment(_ => true, _root, [], canWrite), "PC");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("nas.local", "https://nas.local:8443/")]
    [InlineData(" nas.local:9000 ", "https://nas.local:9000/")]
    [InlineData("https://nas.local", "https://nas.local:8443/")]
    [InlineData("https://nas.local:443/", "https://nas.local/")] // ausdrücklich 443: Standardport, bleibt
    [InlineData("http://localhost:5000", "http://localhost:5000/")]
    [InlineData("ftp://nas", null)]
    [InlineData("", null)]
    public void Addresses_are_completed_to_https_with_the_default_port(string input, string? expected) =>
        Assert.Equal(expected, ClientSetup.ParseAddress(input)?.ToString());

    [Fact]
    public async Task An_unknown_certificate_needs_confirmation_and_is_pinned_afterwards()
    {
        _server.Trusted = false;

        var first = await _setup.SignInAsync(new SignInRequest("nas.local", "anna", "pw"), default);
        Assert.Equal((SetupErrors.UntrustedCertificate, "abc123"), (first.ErrorCode, first.CertificateSha256));
        Assert.Empty(_server.Registrations); // ohne Bestätigung geht das Passwort nicht über die Leitung

        var second = await _setup.SignInAsync(new SignInRequest("nas.local", "anna", "pw", "ABC123"), default);

        Assert.True(second.Succeeded);
        var connection = Assert.Single(await _config.ListConnectionsAsync(default));
        Assert.Equal(("abc123", "nas.local", "d1"), (connection.PinnedCertificateSha256, connection.DisplayName, connection.DeviceId));
        Assert.Equal("token-1", await _secrets.GetAsync(CredentialKeys.DeviceToken(connection.Id), default));
        Assert.Equal("abc123", _server.Registrations[0].Pin);
    }

    [Fact]
    public async Task Wrong_passwords_and_unreachable_servers_are_told_apart_and_a_second_sign_in_reuses_the_connection()
    {
        _server.Password = "richtig";
        Assert.Equal(SetupErrors.SignInFailed, (await _setup.SignInAsync(new SignInRequest("nas", "anna", "falsch"), default)).ErrorCode);

        _server.Reachable = false;
        Assert.Equal(SetupErrors.Unreachable, (await _setup.SignInAsync(new SignInRequest("nas", "anna", "richtig"), default)).ErrorCode);

        _server.Reachable = true;
        var first = await _setup.SignInAsync(new SignInRequest("nas", "anna", "richtig"), default);
        var again = await _setup.SignInAsync(new SignInRequest("nas", "ANNA", "richtig"), default);

        Assert.Equal(first.ConnectionId, again.ConnectionId);
        Assert.Equal("token-2", await _secrets.GetAsync(CredentialKeys.DeviceToken(first.ConnectionId!), default));
    }

    [Fact]
    public async Task Browsing_lists_spaces_then_only_folders_one_level_deep()
    {
        var connection = (await _setup.SignInAsync(new SignInRequest("nas", "anna", "pw"), default)).ConnectionId!;

        var spaces = await _setup.BrowseAsync(connection, null, string.Empty, default);
        var folders = await _setup.BrowseAsync(connection, "team", "Projekte", default);

        Assert.Equal([("My Drive", true), ("Team", false)], spaces.Select(s => (s.Name, s.CanWrite)));
        Assert.Equal([("Projekte/2026", "2026", false)], folders.Select(f => (f.Path, f.Name, f.CanWrite)));
    }

    [Fact]
    public async Task Creating_a_task_checks_the_rules_creates_the_folder_and_starts_it_deleting_stops_it()
    {
        var connection = (await _setup.SignInAsync(new SignInRequest("nas", "anna", "pw"), default)).ConnectionId!;
        var local = Path.Combine(_root, "Drive");

        var created = await _setup.CreateTaskAsync(new NewTaskRequest(connection, "my", string.Empty, local), default);
        Assert.True(created.Succeeded);
        Assert.True(Directory.Exists(local));
        Assert.Equal(created.TaskId, Assert.Single(_lifecycle.Started).Id);

        var nested = await _setup.CreateTaskAsync(new NewTaskRequest(connection, "my", "x", Path.Combine(local, "Unter")), default);
        var readOnly = await _setup.CreateTaskAsync(new NewTaskRequest(connection, "team", "Projekte", Path.Combine(_root, "Team")), default);
        var readOnlyOk = await _setup.CreateTaskAsync(
            new NewTaskRequest(connection, "team", "Projekte", Path.Combine(_root, "Team"), SyncDirection.DownloadOnly), default);

        Assert.Equal([TaskRuleViolation.LocalFolderOverlapsTask], nested.Violations);
        Assert.Equal([TaskRuleViolation.ReadOnlyRequiresDownloadOnly], readOnly.Violations);
        Assert.True(readOnlyOk.Succeeded);
        Assert.Equal([TaskRuleViolation.LocalFolderOverlapsTask], await _setup.CheckLocalFolderAsync(Path.Combine(local, "Unter"), false, default));

        var summary = (await _setup.GetTasksAsync(default)).Single(t => t.TaskId == created.TaskId);
        Assert.Equal(("nas", "nas:8443", local), (summary.ServerName, summary.ServerAddress, summary.LocalPath));

        await _setup.DeleteTaskAsync(created.TaskId!, default);
        Assert.Equal([created.TaskId!], _lifecycle.Stopped);
        Assert.DoesNotContain(await _setup.GetTasksAsync(default), t => t.TaskId == created.TaskId);
        Assert.True(Directory.Exists(local)); // lokale Dateien bleiben
    }

    [Fact]
    public async Task Deleting_a_task_releases_its_sync_root_only_after_the_task_stopped()
    {
        var cloud = new CloudFolders(_lifecycle);
        var setup = new ClientSetup(
            _config, _secrets, _server, _lifecycle, canWrite => new TaskEnvironment(_ => true, _root, [], canWrite), "PC", cloudFolders: cloud);
        var connection = (await setup.SignInAsync(new SignInRequest("nas", "anna", "pw"), default)).ConnectionId!;
        var created = await setup.CreateTaskAsync(new NewTaskRequest(connection, "my", string.Empty, Path.Combine(_root, "Drive")), default);

        await setup.DeleteTaskAsync(created.TaskId!, default);

        Assert.Equal([$"{created.TaskId} angehalten={true}"], cloud.Detached);
    }

    [Fact]
    public async Task Signing_in_again_keeps_the_connection_takes_the_new_address_and_restarts_its_tasks()
    {
        var connection = (await _setup.SignInAsync(new SignInRequest("nas", "anna", "pw"), default)).ConnectionId!;
        var created = await _setup.CreateTaskAsync(new NewTaskRequest(connection, "my", string.Empty, Path.Combine(_root, "Drive")), default);

        var wrong = await _setup.UpdateConnectionAsync(connection, new SignInRequest("nas-neu", "anna", "falsch"), default);
        var again = await _setup.UpdateConnectionAsync(connection, new SignInRequest("nas-neu:9000", "anna", "pw"), default);
        var unknown = await _setup.UpdateConnectionAsync("c_gibts_nicht", new SignInRequest("nas", "anna", "pw"), default);

        Assert.Equal((SetupErrors.SignInFailed, connection, SetupErrors.UnknownConnection), (wrong.ErrorCode, again.ConnectionId, unknown.ErrorCode));
        var stored = Assert.Single(await _config.ListConnectionsAsync(default));
        Assert.Equal(("https://nas-neu:9000/", "d2"), (stored.ServerUrl.ToString(), stored.DeviceId));
        Assert.Equal("token-2", await _secrets.GetAsync(CredentialKeys.DeviceToken(connection), default));
        Assert.Equal([created.TaskId!], _lifecycle.Stopped);
        Assert.Equal(2, _lifecycle.Started.Count(t => t.Id == created.TaskId));
    }

    [Fact]
    public async Task Sync_rules_change_mode_and_filters_but_not_the_place_and_restart_the_task()
    {
        var connection = (await _setup.SignInAsync(new SignInRequest("nas", "anna", "pw"), default)).ConnectionId!;
        var local = Path.Combine(_root, "Team");
        var created = await _setup.CreateTaskAsync(new NewTaskRequest(connection, "team", "Projekte", local, SyncDirection.DownloadOnly), default);
        var task = (await _setup.GetTaskAsync(created.TaskId!, default))!;

        var forbidden = await _setup.UpdateTaskAsync(task with { Direction = SyncDirection.TwoWay }, default); // nur Leserecht
        var filters = new TaskFilterSettings(1024, ["Thumbs.db"], ["iso"], [RelativePath.Parse("Archiv")]);
        var ok = await _setup.UpdateTaskAsync(task with { LocalPath = @"C:\woanders", AdvancedConsistencyCheck = true, Filters = filters }, default);

        Assert.Equal([TaskRuleViolation.ReadOnlyRequiresDownloadOnly], forbidden.Violations);
        Assert.True(ok.Succeeded);
        var stored = (await _setup.GetTaskAsync(created.TaskId!, default))!;
        Assert.Equal((local, true, "Archiv"), (stored.LocalPath, stored.AdvancedConsistencyCheck, stored.Filters!.ExcludedFolders!.Single().Value));
        Assert.Equal([created.TaskId!], _lifecycle.Stopped);
        Assert.Equal(SetupErrors.UnknownTask, (await _setup.UpdateTaskAsync(task with { Id = "t_x" }, default)).ErrorCode);
    }

    [Fact]
    public async Task Usage_is_reported_per_task_and_the_proxy_password_lives_in_the_secret_store()
    {
        var connection = (await _setup.SignInAsync(new SignInRequest("nas", "anna", "pw"), default)).ConnectionId!;
        var created = await _setup.CreateTaskAsync(new NewTaskRequest(connection, "my", string.Empty, Path.Combine(_root, "Drive")), default);
        await _setup.SaveSettingsAsync(new ClientSettings { Proxy = new(ProxyMode.Manual, "proxy.lan", 3128, true, "bob") }, default);

        await _setup.SetProxyPasswordAsync("geheim", default);
        var proxy = await ClientSetup.ProxyAsync(_config, _secrets, default);
        var usage = Assert.Single(await _setup.GetUsageAsync(default));

        Assert.Equal((created.TaskId, 2048L, 2L), (usage.TaskId, usage.Bytes, usage.Files));
        Assert.Equal(("proxy.lan", 3128, "bob", "geheim"), (proxy.Host, proxy.Port, proxy.UserName, proxy.Password));
        Assert.DoesNotContain("geheim", System.Text.Json.JsonSerializer.Serialize(await _setup.GetSettingsAsync(default)), StringComparison.Ordinal);
        Assert.Equal(2, _lifecycle.Stopped.Count); // Proxy-Wechsel und Passwort starten die Aufgaben neu

        await _setup.SetProxyPasswordAsync(null, default);
        Assert.Null((await ClientSetup.ProxyAsync(_config, _secrets, default)).Password);
    }

    [Fact]
    public async Task The_notification_center_collects_all_servers_sets_the_red_dot_and_marks_read()
    {
        var connection = (await _setup.SignInAsync(new SignInRequest("nas", "anna", "pw"), default)).ConnectionId!;
        _server.Notes.Add(new NotificationInfo("n1", "shared", DateTimeOffset.UnixEpoch.AddDays(2), "ben", "team", string.Empty, Read: false));
        _server.Notes.Add(new NotificationInfo("n2", "unlock_request", DateTimeOffset.UnixEpoch.AddDays(1), "carla", "my", "a.txt", Read: true));
        var daemon = new DaemonController();
        using var center = new NotificationCenter(_config, _secrets, _server, daemon);
        var setup = new ClientSetup(_config, _secrets, _server, _lifecycle, _ => new TaskEnvironment(_ => true, _root, [], true), "PC", notifications: center);

        var list = await setup.GetNotificationsAsync(default);

        Assert.Equal([("n1", "Team", "nas"), ("n2", "My Drive", "nas")], list.Select(n => (n.Id, n.SpaceName, n.ServerName)));
        Assert.Equal(connection, list[0].ConnectionId);
        Assert.True(daemon.HasNotifications);

        await setup.MarkNotificationsReadAsync(default);

        Assert.False(daemon.HasNotifications);
        Assert.All(_server.Notes, n => Assert.True(n.Read));
    }

    private sealed class FakeServer : IServerAccess
    {
        public bool Trusted { get; set; } = true;

        public bool Reachable { get; set; } = true;

        public string Password { get; set; } = "pw";

        public List<(string User, string? Pin)> Registrations { get; } = [];

        public Task<Result<CertificateCheck>> ProbeAsync(Uri server, CancellationToken ct) => Task.FromResult(
            Reachable ? Result.Ok(new CertificateCheck(Trusted, "abc123")) : Result.Fail<CertificateCheck>(SyncErrorKind.Transient, "network", "weg"));

        public Task<Result<DeviceRegistration>> RegisterDeviceAsync(
            Uri server, string userName, string password, string deviceName, string? pinnedCertificateSha256, CancellationToken ct)
        {
            if (password != Password)
            {
                return Task.FromResult(Result.Fail<DeviceRegistration>(SyncErrorKind.Permanent, ErrorCode.Unauthorized, "nein"));
            }

            Registrations.Add((userName, pinnedCertificateSha256));
            return Task.FromResult(Result.Ok(new DeviceRegistration("d" + Registrations.Count, "token-" + Registrations.Count)));
        }

        public Task<Result<IReadOnlyList<SpaceSummary>>> ListSpacesAsync(ServerLogin login, CancellationToken ct) =>
            Task.FromResult(Result.Ok<IReadOnlyList<SpaceSummary>>([new("my", "My Drive", "owner"), new("team", "Team", "viewer")]));

        public List<NotificationInfo> Notes { get; } = [];

        public Task<Result<IReadOnlyList<NotificationInfo>>> ListNotificationsAsync(ServerLogin login, CancellationToken ct) =>
            Task.FromResult(Result.Ok<IReadOnlyList<NotificationInfo>>([.. Notes]));

        public Task<Result<bool>> MarkNotificationReadAsync(ServerLogin login, string notificationId, CancellationToken ct)
        {
            var index = Notes.FindIndex(n => n.Id == notificationId);
            Notes[index] = Notes[index] with { Read = true };
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<IReadOnlyList<VersionInfo>>> ListVersionsAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct) =>
            Task.FromResult(Result.Ok<IReadOnlyList<VersionInfo>>([]));

        public Task<Result<CommitResponse>> RestoreVersionAsync(
            ServerLogin login, string spaceId, RelativePath path, long revision, long baseRevision, CancellationToken ct) =>
            Task.FromResult(Result.Ok(new CommitResponse(revision + 1, "n")));

        public Task<Result<NodeInfo?>> GetNodeInfoAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct) =>
            Task.FromResult(Result.Ok<NodeInfo?>(null));

        public Task<Result<bool>> LockAsync(ServerLogin login, string spaceId, RelativePath path, LockAction action, CancellationToken ct) =>
            Task.FromResult(Result.Ok(true));

        public Task<Result<Stream>> DownloadBlockAsync(ServerLogin login, string spaceId, string hash, CancellationToken ct) =>
            Task.FromResult(Result.Ok<Stream>(new MemoryStream()));

        public Task<Result<UsageResponse>> GetUsageAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct) =>
            Task.FromResult(Result.Ok(new UsageResponse(spaceId == "my" ? 2048 : 0, spaceId == "my" ? 2 : 0)));

        public Task<Result<IReadOnlyList<SnapshotEntry>>> ListChildrenAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct) =>
            Task.FromResult(Result.Ok<IReadOnlyList<SnapshotEntry>>(
            [
                new("n1", path.Value + "/2026", NodeKinds.Directory, 0, DateTimeOffset.UnixEpoch, null, 1),
                new("n2", path.Value + "/liesmich.txt", NodeKinds.File, 3, DateTimeOffset.UnixEpoch, "h", 1),
            ]));
    }

    private sealed class CloudFolders(Lifecycle lifecycle) : ICloudFolders
    {
        public List<string> Detached { get; } = [];

        public Task<CloudFolder> AttachAsync(SyncTaskDefinition task, App.Sync.Hydrator hydrator, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DetachAsync(SyncTaskDefinition task, CancellationToken ct)
        {
            Detached.Add($"{task.Id} angehalten={lifecycle.Stopped.Contains(task.Id)}");
            return Task.CompletedTask;
        }
    }

    private sealed class Lifecycle : ITaskLifecycle
    {
        public List<SyncTaskDefinition> Started { get; } = [];

        public List<string> Stopped { get; } = [];

        public void Start(SyncTaskDefinition task) => Started.Add(task);

        public Task StopAsync(string taskId)
        {
            Stopped.Add(taskId);
            return Task.CompletedTask;
        }
    }

    private sealed class Secrets : ICredentialStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public Task SaveAsync(string key, string secret, CancellationToken ct)
        {
            _values[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult(_values.GetValueOrDefault(key));

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}
