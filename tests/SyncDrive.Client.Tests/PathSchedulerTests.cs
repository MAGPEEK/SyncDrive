using System.Collections.Concurrent;
using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.Tests;

public class PathSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static RelativePath P(string p) => RelativePath.Parse(p);

    [Fact]
    public async Task Every_enqueued_path_is_handled()
    {
        var seen = new ConcurrentBag<string>();
        var scheduler = new PathScheduler((p, _) => { seen.Add(p.Value); return Task.CompletedTask; }, 4);

        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            scheduler.Enqueue(P(name));
        }

        await scheduler.WhenIdleAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(["a", "b", "c", "d", "e"], seen.Order());
        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public async Task Duplicate_events_for_a_waiting_path_are_coalesced_ignoring_case()
    {
        var gate = new TaskCompletionSource();
        var runs = new ConcurrentBag<string>();
        var scheduler = new PathScheduler(async (p, _) => { runs.Add(p.Value); await gate.Task; }, 1);

        scheduler.Enqueue(P("blocker"));
        scheduler.Enqueue(P("x.txt"));
        scheduler.Enqueue(P("X.TXT"));
        scheduler.Enqueue(P("x.txt"));
        gate.SetResult();

        await scheduler.WhenIdleAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(2, runs.Count);
        Assert.Contains("x.txt", runs, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_path_never_runs_concurrently()
    {
        var active = 0;
        var maxActive = 0;
        var runs = 0;
        var scheduler = new PathScheduler(async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(30, CancellationToken.None);
            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref runs);
        }, 8);

        for (var i = 0; i < 5; i++)
        {
            scheduler.Enqueue(P("same.txt"));
            await Task.Delay(10);
        }

        await scheduler.WhenIdleAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(1, maxActive);
        Assert.InRange(runs, 2, 5);
    }

    [Fact]
    public async Task Different_paths_run_in_parallel_up_to_the_limit()
    {
        var active = 0;
        var maxActive = 0;
        var scheduler = new PathScheduler(async (_, ct) =>
        {
            InterlockedMax(ref maxActive, Interlocked.Increment(ref active));
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref active);
        }, 3);

        for (var i = 0; i < 9; i++)
        {
            scheduler.Enqueue(P($"f{i}"));
        }

        await scheduler.WhenIdleAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(3, maxActive);
    }

    [Fact]
    public async Task A_new_event_cancels_the_running_pass_and_reruns_the_path()
    {
        var started = new TaskCompletionSource();
        var cancelled = false;
        var runs = 0;
        var scheduler = new PathScheduler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            }
        }, 2);

        scheduler.Enqueue(P("big.iso"));
        await started.Task.WaitAsync(Timeout);
        scheduler.Enqueue(P("big.iso"));

        await scheduler.WhenIdleAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.True(cancelled);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Handler_errors_are_reported_and_do_not_stop_the_queue()
    {
        var errors = new ConcurrentBag<string>();
        var done = new ConcurrentBag<string>();
        var scheduler = new PathScheduler(
            (p, _) => p.Value == "bad" ? throw new InvalidOperationException("kaputt") : Task.Run(() => done.Add(p.Value), CancellationToken.None),
            2,
            (p, _) => errors.Add(p.Value));

        scheduler.Enqueue(P("bad"));
        scheduler.Enqueue(P("good"));

        await scheduler.WhenIdleAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(["bad"], errors);
        Assert.Equal(["good"], done);
    }

    [Fact]
    public async Task An_idle_queue_reports_idle_immediately_and_becomes_busy_again()
    {
        var scheduler = new PathScheduler((_, ct) => Task.Delay(20, ct), 1);

        Assert.True(scheduler.WhenIdleAsync(CancellationToken.None).IsCompleted);
        scheduler.Enqueue(P("a"));
        Assert.False(scheduler.WhenIdleAsync(CancellationToken.None).IsCompleted);
        await scheduler.WhenIdleAsync(CancellationToken.None).WaitAsync(Timeout);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
