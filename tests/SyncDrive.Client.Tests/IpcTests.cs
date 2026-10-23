using System.IO.Pipes;
using System.Text;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.Tests.Scenarios;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.Fs;
using SyncDrive.Infra.Ipc;

namespace SyncDrive.Client.Tests;

/// <summary>Daemon-Steuerung direkt und über die echte Named Pipe, so wie Oberfläche und Daemon später reden.</summary>
public sealed class IpcTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly Cluster _cluster = new();
    private readonly List<FileSystemChangeSource> _sources = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipe = "syncdrive-test-" + Guid.NewGuid().ToString("N");
    private readonly DaemonController _daemon = new();
    private Task? _server;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_server is not null)
        {
            await _server.WaitAsync(Wait);
        }

        _sources.ForEach(s => s.Dispose());
        _cluster.Dispose();
        _stop.Dispose();
    }

    private TestClient AddTask(string id)
    {
        var client = _cluster.AddClient(id);
        var source = new FileSystemChangeSource(client.Root);
        _sources.Add(source);
        var runner = new SyncRunner(client.Session, source, SyncRunnerOptions.Default, observer: client.Tracker);
        _daemon.Register(id, runner, client.Tracker, client.Log);
        return client;
    }

    private async Task<IpcClient> ConnectAsync()
    {
        _server ??= new IpcServer(_pipe, _daemon).RunAsync(_stop.Token);
        return await IpcClient.ConnectAsync(_pipe, Wait, _stop.Token);
    }

    [Fact]
    public async Task The_controller_combines_its_tasks_and_pauses_one_or_all()
    {
        var a = AddTask("A");
        var b = AddTask("B");
        a.Tracker.Connected();
        b.Tracker.Connected();
        Assert.Equal(TrayState.UpToDate, (await _daemon.GetStatusAsync(default)).Tray);

        await _daemon.PauseAsync("A", default);
        var status = await _daemon.GetStatusAsync(default);
        Assert.Equal([TaskState.Paused, TaskState.Done], status.Tasks.Select(t => t.State));
        Assert.Equal(TrayState.UpToDate, status.Tray);

        await _daemon.PauseAsync(null, default);
        Assert.Equal(TrayState.Paused, (await _daemon.GetStatusAsync(default)).Tray);

        await Assert.ThrowsAsync<UnknownTaskException>(() => _daemon.ResumeAsync("gibt-es-nicht", default));
    }

    [Fact]
    public async Task Status_activity_and_pause_travel_over_the_pipe()
    {
        var a = AddTask("A");
        a.Tracker.Connected();
        await a.Log.AppendAsync(new ActivityEntry(DateTimeOffset.UtcNow, ActivityAction.Upload, RelativePath.Parse("Ordner/ä.txt"), false, ActivityResult.Done), default);
        await using var ui = await ConnectAsync();

        var status = await ui.GetStatusAsync(default);
        Assert.Equal((TrayState.UpToDate, "A", TaskState.Done), (status.Tray, status.Tasks.Single().TaskId, status.Tasks.Single().State));

        var activity = Assert.Single(await ui.GetRecentActivityAsync(10, default));
        Assert.Equal(("A", "Ordner/ä.txt", ActivityAction.Upload), (activity.TaskId, activity.Entry.Path.Value, activity.Entry.Action));

        await ui.PauseAsync("A", default);
        Assert.Equal(TrayState.Paused, (await ui.GetStatusAsync(default)).Tray);
        await ui.ResumeAsync(null, default);
        Assert.Equal(TrayState.UpToDate, (await ui.GetStatusAsync(default)).Tray);

        var error = await Assert.ThrowsAsync<IpcException>(() => ui.PauseAsync("X", default));
        Assert.Equal("unknown_task", error.Code);
    }

    [Fact]
    public async Task A_watcher_gets_the_current_status_and_every_change_including_notifications()
    {
        var a = AddTask("A");
        await using var ui = await ConnectAsync();
        await using var updates = ui.WatchStatusAsync(_stop.Token).GetAsyncEnumerator();

        Assert.True(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(TrayState.Disconnected, updates.Current.Tray); // Aufgabe verbindet noch

        a.Tracker.Connected();
        await NextAsync(updates, s => s.Tray == TrayState.UpToDate);

        a.Tracker.PathStarted(RelativePath.Parse("x.txt"));
        var syncing = await NextAsync(updates, s => s.Tray == TrayState.Syncing);
        Assert.Equal("x.txt", syncing.Tasks.Single().CurrentPath);

        _daemon.HasNotifications = true;
        await NextAsync(updates, s => s.HasNotifications);
    }

    [Fact]
    public async Task Several_windows_can_talk_to_the_daemon_at_the_same_time()
    {
        AddTask("A").Tracker.Connected();
        await using var panel = await ConnectAsync();
        await using var settings = await IpcClient.ConnectAsync(_pipe, Wait, _stop.Token);

        var both = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? panel : settings).GetStatusAsync(default)));

        Assert.All(both, s => Assert.Equal(TrayState.UpToDate, s.Tray));
    }

    [Fact]
    public async Task When_the_daemon_stops_the_client_reports_disconnected()
    {
        AddTask("A");
        await using var ui = await ConnectAsync();
        var gone = new TaskCompletionSource();
        ui.Disconnected += () => gone.TrySetResult();

        await _stop.CancelAsync();
        await gone.Task.WaitAsync(Wait);

        Assert.False(ui.IsConnected);
        var error = await Assert.ThrowsAsync<IpcException>(() => ui.GetStatusAsync(default));
        Assert.Equal("disconnected", error.Code);
    }

    [Fact]
    public async Task Other_versions_and_garbage_are_rejected_without_closing_the_connection()
    {
        _server = new IpcServer(_pipe, _daemon).RunAsync(_stop.Token);
        await using var pipe = new NamedPipeClientStream(".", IpcProtocol.Endpoint(_pipe), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(Wait, _stop.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("""{"v":2,"id":1,"method":"status.get"}""");
        Assert.Contains("unsupported_version", await reader.ReadLineAsync().WaitAsync(Wait), StringComparison.Ordinal);

        await writer.WriteLineAsync("kein json");
        Assert.Contains("bad_request", await reader.ReadLineAsync().WaitAsync(Wait), StringComparison.Ordinal);

        await writer.WriteLineAsync("""{"v":1,"id":2,"method":"files.delete"}""");
        Assert.Contains("unknown_method", await reader.ReadLineAsync().WaitAsync(Wait), StringComparison.Ordinal);

        await writer.WriteLineAsync("""{"v":1,"id":3,"method":"status.get"}""");
        Assert.Contains("\"ok\":true", await reader.ReadLineAsync().WaitAsync(Wait), StringComparison.Ordinal);
    }

    [Fact]
    public void The_pipe_name_is_per_user_and_does_not_reveal_the_user_name()
    {
        var name = IpcProtocol.DefaultPipeName("Erika");

        Assert.Equal(name, IpcProtocol.DefaultPipeName("ERIKA"));
        Assert.NotEqual(name, IpcProtocol.DefaultPipeName("Max"));
        Assert.DoesNotContain("erika", name, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<OverallStatus> NextAsync(IAsyncEnumerator<OverallStatus> updates, Func<OverallStatus, bool> until)
    {
        while (await updates.MoveNextAsync().AsTask().WaitAsync(Wait))
        {
            if (until(updates.Current))
            {
                return updates.Current;
            }
        }

        throw new InvalidOperationException("Beobachtung endete vorzeitig.");
    }
}
