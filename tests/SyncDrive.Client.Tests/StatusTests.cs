using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;
using TaskStatus = SyncDrive.Client.App.Status.TaskStatus;

namespace SyncDrive.Client.Tests;

public class StatusTests
{
    private static readonly RelativePath P = RelativePath.Parse("a.txt");

    private static TaskStatus ToStatus(TaskState state) => new("t", state, 0, null, [], null, null);

    private static Result<PathOutcome> Ok() => Result.Ok(new PathOutcome(P, OutcomeKind.Uploaded));

    private static Result<PathOutcome> Fail(SyncErrorKind kind, string code) => Result.Fail<PathOutcome>(kind, code, "x");

    [Theory]
    [InlineData(new TaskState[0], TrayState.Normal)]
    [InlineData(new[] { TaskState.Done, TaskState.Done }, TrayState.UpToDate)]
    [InlineData(new[] { TaskState.Done, TaskState.Syncing }, TrayState.Syncing)]
    [InlineData(new[] { TaskState.Paused, TaskState.Disabled }, TrayState.Paused)]
    [InlineData(new[] { TaskState.Paused, TaskState.Done }, TrayState.UpToDate)]
    [InlineData(new[] { TaskState.Syncing, TaskState.Connecting }, TrayState.Disconnected)]
    [InlineData(new[] { TaskState.Connecting, TaskState.Error }, TrayState.Error)]
    public void Tasks_combine_into_one_tray_state(TaskState[] states, TrayState expected)
    {
        Assert.Equal(expected, OverallStatus.Combine([.. states.Select(ToStatus)], hasNotifications: false).Tray);
    }

    [Fact]
    public void A_task_starts_connecting_becomes_done_after_connecting_and_syncing_while_busy()
    {
        var tracker = new TaskStatusTracker("t");
        Assert.Equal(TaskState.Connecting, tracker.Snapshot.State);

        tracker.Connected();
        Assert.Equal(TaskState.Done, tracker.Snapshot.State);

        tracker.PathStarted(P);
        Assert.Equal((TaskState.Syncing, 1, "a.txt"), (tracker.Snapshot.State, tracker.Snapshot.Pending, tracker.Snapshot.CurrentPath));

        tracker.PathFinished(P, Ok());
        Assert.Equal((TaskState.Done, 0), (tracker.Snapshot.State, tracker.Snapshot.Pending));
        Assert.NotNull(tracker.Snapshot.LastSyncedAt);
    }

    [Fact]
    public void Network_failures_mean_connecting_and_permanent_failures_mean_error_until_the_path_succeeds()
    {
        var tracker = new TaskStatusTracker("t");
        tracker.Connected();

        tracker.PathStarted(P);
        tracker.PathFinished(P, Fail(SyncErrorKind.Transient, RemoteErrors.Network));
        Assert.Equal(TaskState.Connecting, tracker.Snapshot.State);

        tracker.PathStarted(P);
        tracker.PathFinished(P, Fail(SyncErrorKind.Permanent, "invalid_path"));
        Assert.Equal((TaskState.Error, "a.txt"), (tracker.Snapshot.State, tracker.Snapshot.FailingPaths.Single()));

        tracker.PathStarted(P);
        tracker.PathFinished(P, Ok());
        Assert.Equal(TaskState.Done, tracker.Snapshot.State);
    }

    [Fact]
    public void Pause_and_stop_take_precedence_and_changes_are_announced()
    {
        var tracker = new TaskStatusTracker("t");
        var seen = new List<TaskState>();
        tracker.Changed += s => seen.Add(s.State);
        tracker.Connected();

        tracker.Paused(true);
        tracker.Paused(false);
        tracker.Stopped(new SyncError(SyncErrorKind.Fatal, "unauthorized", "Gerät gesperrt"));

        Assert.Equal([TaskState.Done, TaskState.Paused, TaskState.Done, TaskState.Disabled], seen);
        Assert.Equal("Gerät gesperrt", tracker.Snapshot.LastError);
    }

    [Fact]
    public async Task The_gate_blocks_while_paused_and_releases_on_resume()
    {
        var gate = new SyncGate();
        await gate.WaitOpenAsync(CancellationToken.None); // offen

        gate.Pause();
        var waiting = gate.WaitOpenAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        Assert.True(gate.IsPaused);

        gate.Resume();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsPaused);
    }
}
