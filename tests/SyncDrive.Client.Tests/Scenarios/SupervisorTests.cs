using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;
using SyncDrive.Infra.Fs;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>Der Überwacher hält Aufgaben am Laufen, startet nach Abstürzen neu und zeigt Aufgaben, die nicht laufen können.</summary>
public sealed class SupervisorTests : IAsyncDisposable
{
    private static readonly SupervisorOptions Fast = new(
        new SyncRunnerOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), 2, TimeSpan.FromSeconds(2)),
        FirstRestartDelay: TimeSpan.FromMilliseconds(200),
        MaxRestartDelay: TimeSpan.FromSeconds(1));

    private readonly Cluster _cluster = new();
    private readonly DaemonController _daemon = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Exception> _crashes = [];
    private Task? _running;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(10));
        }

        _cluster.Dispose();
        _stop.Dispose();
    }

    private static SyncTaskDefinition Task(string id, bool paused = false) =>
        new(id, "c1", Cluster.Space, RelativePath.Root, "-", Paused: paused);

    private void Run(ITaskComponentsFactory factory, params SyncTaskDefinition[] tasks)
    {
        var supervisor = new TaskSupervisor(_daemon, factory, Fast with { OnCrash = (_, e) => _crashes.Add(e) });
        _running = supervisor.RunAsync(tasks, _stop.Token);
    }

    private async Task<OverallStatus> WaitForAsync(Func<OverallStatus, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var status = await _daemon.GetStatusAsync(default);
            if (condition(status))
            {
                return status;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Zeitüberschreitung: {what}. Stand: {string.Join(", ", status.Tasks.Select(t => $"{t.TaskId}={t.State} {t.LastError}"))}");
            await System.Threading.Tasks.Task.Delay(50);
        }
    }

    [Fact]
    public async Task Tasks_run_under_supervision_and_sync_between_devices()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        Run(new ClusterFactory(new() { ["A"] = a, ["B"] = b }), Task("A"), Task("B"));
        await WaitForAsync(s => s.Tasks.Count == 2 && s.Tray == TrayState.UpToDate, "beide Aufgaben verbunden");

        a.Write("über-den-daemon.txt", "läuft");

        await WaitForAsync(_ => b.Exists("über-den-daemon.txt"), "Datei erreicht B");
    }

    [Fact]
    public async Task A_crash_while_building_is_retried_and_reported()
    {
        var a = _cluster.AddClient("A");
        var factory = new ClusterFactory(new() { ["A"] = a }) { FailuresBeforeSuccess = 2 };
        Run(factory, Task("A"));

        await WaitForAsync(s => s.Tasks is [{ State: TaskState.Done }], "nach zwei Abstürzen läuft A");

        Assert.Equal(2, _crashes.Count);
        Assert.Equal(3, factory.Attempts);
    }

    [Fact]
    public async Task A_task_without_sign_in_shows_as_disabled_with_the_reason_and_is_not_restarted()
    {
        var factory = new ClusterFactory([]) { Stop = new SyncError(SyncErrorKind.Fatal, DaemonErrors.NotSignedIn, "Bitte erneut anmelden.") };
        Run(factory, Task("A"));

        var status = await WaitForAsync(s => s.Tasks is [{ State: TaskState.Disabled }], "A deaktiviert");
        await System.Threading.Tasks.Task.Delay(500);

        Assert.Equal("Bitte erneut anmelden.", status.Tasks[0].LastError);
        Assert.Equal(1, factory.Attempts);
        Assert.Empty(_crashes);
    }

    [Fact]
    public async Task A_task_saved_as_paused_starts_paused_and_resumes_on_request()
    {
        var a = _cluster.AddClient("A");
        a.Write("wartet.txt", "erst nach Fortsetzen");
        Run(new ClusterFactory(new() { ["A"] = a }), Task("A", paused: true));

        await WaitForAsync(s => s.Tray == TrayState.Paused, "A pausiert");
        await System.Threading.Tasks.Task.Delay(300);
        Assert.Empty(a.Remote.Commits);

        await _daemon.ResumeAsync("A", default);
        await WaitForAsync(_ => !a.Remote.Commits.IsEmpty, "nach Fortsetzen wird hochgeladen");
    }

    /// <summary>Baut Aufgaben aus Cluster-Clients; kann scheitern oder stoppen wie die echte Fabrik.</summary>
    private sealed class ClusterFactory(Dictionary<string, TestClient> clients) : ITaskComponentsFactory
    {
        public int FailuresBeforeSuccess { get; init; }

        public SyncError? Stop { get; init; }

        public int Attempts { get; private set; }

        public Task<TaskComponents> CreateAsync(SyncTaskDefinition task, CancellationToken ct)
        {
            Attempts++;
            if (Stop is { } stop)
            {
                throw new SyncStoppedException(stop);
            }

            if (Attempts <= FailuresBeforeSuccess)
            {
                throw new IOException("Datenbank gesperrt");
            }

            var client = clients[task.Id];
            var changes = new FileSystemChangeSource(client.Root);
            return System.Threading.Tasks.Task.FromResult(
                new TaskComponents(client.Session, changes, client.Log, client.Tracker, new Disposer(changes)));
        }

        private sealed class Disposer(IDisposable inner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                inner.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
