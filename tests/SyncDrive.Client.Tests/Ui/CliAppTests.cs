using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.Cli;
using SyncDrive.Core.Merge;
using TaskStatus = SyncDrive.Client.App.Status.TaskStatus;

namespace SyncDrive.Client.Tests.Ui;

/// <summary><c>syncdrive_cli</c> gegen einen vorgespielten Daemon: Aufrufe, Ausgaben, Rückgabewerte.</summary>
public sealed class CliAppTests : IDisposable
{
    private readonly FakeDaemon _daemon = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private string? _password = "geheim";

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
    }

    private Task<int> RunAsync(params string[] args) =>
        new CliApp(_ => Task.FromResult<IDaemonClient>(_daemon), _output, _error, () => _password).RunAsync(args, default);

    private void OneConnection() => _daemon.Connections.Add(new ConnectionSummary("c1", "NAS", "nas.local:8443", "anna"));

    [Fact]
    public async Task Help_and_version_need_no_daemon_and_no_command_is_a_usage_error()
    {
        var cli = new CliApp(_ => throw new IOException("kein Daemon"), _output, _error, () => null);

        Assert.Equal(CliApp.Ok, await cli.RunAsync(["version"], default));
        Assert.Equal(CliApp.Ok, await cli.RunAsync(["help"], default));
        Assert.Equal(CliApp.Usage, await cli.RunAsync([], default));
        Assert.Contains("syncdrive_cli 0.", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("desync <task-id|local-folder>", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_daemon_is_reported_not_thrown()
    {
        var cli = new CliApp(_ => throw new TimeoutException("Zeit"), _output, _error, () => null);

        Assert.Equal(CliApp.Failed, await cli.RunAsync(["status"], default));
        Assert.Contains("not running", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_signs_in_and_explains_an_untrusted_certificate()
    {
        _daemon.SignInResults.Enqueue(new SignInOutcome(null, SetupErrors.UntrustedCertificate, CertificateSha256: "AB:CD"));
        _daemon.SignInResults.Enqueue(new SignInOutcome("c1"));

        var untrusted = await RunAsync("connect", "nas.local", "-u", "anna");
        var trusted = await RunAsync("connect", "nas.local", "--user", "anna", "--trust", "AB:CD");

        Assert.Equal((CliApp.Failed, CliApp.Ok), (untrusted, trusted));
        Assert.Contains("AB:CD", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Connected: c1", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(new SignInRequest("nas.local", "anna", "geheim", "AB:CD"), _daemon.SignIns[1]);
    }

    [Fact]
    public async Task Connect_without_user_or_password_is_a_usage_error()
    {
        _password = null;

        Assert.Equal(CliApp.Usage, await RunAsync("connect", "nas.local"));
        Assert.Equal(CliApp.Usage, await RunAsync("connect", "nas.local", "-u", "anna"));
        Assert.Equal(CliApp.Usage, await RunAsync("connect", "nas.local", "-u"));
        Assert.Empty(_daemon.SignIns);
    }

    [Fact]
    public async Task Ls_lists_spaces_and_folders_of_the_only_connection()
    {
        OneConnection();
        _daemon.Folders[":"] = [new ServerFolder("sp_1", string.Empty, "My Drive", true, true)];
        _daemon.Folders["sp_1:Projekte"] = [new ServerFolder("sp_1", "Projekte/2026", "2026", false, false)];

        await RunAsync("ls");
        await RunAsync("ls", "sp_1", "/Projekte/");

        Assert.Equal(["sp_1\trw\tMy Drive", "Projekte/2026/\tro"], Lines(_output));
    }

    [Fact]
    public async Task Several_connections_need_c_which_accepts_id_name_or_address()
    {
        OneConnection();
        _daemon.Connections.Add(new ConnectionSummary("c2", "Büro", "office:8443", "anna"));

        var ambiguous = await RunAsync("ls");
        var byName = await RunAsync("ls", "-c", "büro");
        var unknown = await RunAsync("ls", "-c", "anderswo");

        Assert.Equal((CliApp.Failed, CliApp.Ok, CliApp.Failed), (ambiguous, byName, unknown));
        Assert.Contains("choose one with -c", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_creates_a_task_with_direction_and_on_demand()
    {
        OneConnection();
        var local = Path.Combine(Path.GetTempPath(), "SyncDrive-CLI");

        var result = await RunAsync("sync", local, "-s", "sp_1", "-p", "/Projekte/", "--direction", "download", "--on-demand");

        Assert.Equal(CliApp.Ok, result);
        Assert.Equal(new NewTaskRequest("c1", "sp_1", "Projekte", local, SyncDirection.DownloadOnly, OnDemand: true), _daemon.Created.Single());
        Assert.Contains("Sync task created: t-neu", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_rejects_an_unknown_direction_before_asking_the_daemon()
    {
        OneConnection();

        Assert.Equal(CliApp.Usage, await RunAsync("sync", "x", "-s", "sp_1", "--direction", "sideways"));
        Assert.Equal(CliApp.Usage, await RunAsync("sync", "x"));
        Assert.Empty(_daemon.Created);
    }

    [Fact]
    public async Task Desync_finds_the_task_by_id_or_local_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Sync-A");
        _daemon.Tasks.Add(Summary("t1", folder));
        _daemon.Tasks.Add(Summary("t2", Path.Combine(Path.GetTempPath(), "Sync-B")));

        var byFolder = await RunAsync("desync", folder + Path.DirectorySeparatorChar);
        var byId = await RunAsync("desync", "t2");
        var missing = await RunAsync("desync", "t9");

        Assert.Equal((CliApp.Ok, CliApp.Ok, CliApp.Failed), (byFolder, byId, missing));
        Assert.Equal(["delete t1", "delete t2"], _daemon.Calls);
    }

    [Fact]
    public async Task Status_shows_each_task_and_fails_on_errors()
    {
        _daemon.Tasks.Add(Summary("t1", "C:\\Sync"));
        _daemon.Status = OverallStatus.Combine(
            [new TaskStatus("t1", TaskState.Error, 0, null, [], "unreachable", null)], false);

        var result = await RunAsync("status");

        Assert.Equal(CliApp.Failed, result);
        Assert.Equal(["Overall: Error", "t1\tC:\\Sync\tError\terror: unreachable"], Lines(_output));
    }

    [Fact]
    public async Task Pause_and_resume_one_task_or_all()
    {
        await RunAsync("pause");
        await RunAsync("resume", "t1");

        Assert.Equal(["pause *", "resume t1"], _daemon.Calls);
    }

    [Fact]
    public async Task Unknown_commands_and_options_without_value_are_usage_errors()
    {
        var withoutDaemon = new CliApp(_ => throw new IOException("kein Daemon"), _output, _error, () => null);

        Assert.Equal(CliApp.Usage, await withoutDaemon.RunAsync(["frobnicate"], default));
        Assert.Equal(CliApp.Usage, await RunAsync("ls", "-c"));
    }

    private static TaskSummary Summary(string id, string local) =>
        new(id, "c1", "NAS", "nas.local:8443", "sp_1", string.Empty, local, SyncDirection.TwoWay, false, false);

    private static string[] Lines(StringWriter writer) => writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
