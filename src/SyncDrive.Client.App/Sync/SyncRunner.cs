using System.Threading.Channels;
using SyncDrive.Client.App.Ports;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Sync;

/// <param name="SettleDelay">Ruhezeit nach einem Dateiereignis, bevor bewertet wird (Editoren schreiben in Etappen).</param>
/// <param name="LongPoll">Wartezeit je Feed-Abfrage.</param>
/// <param name="RescanInterval">Abstand des periodischen Rescans (Sicherheitsnetz gegen verlorene Ereignisse).</param>
/// <param name="TrashMaxAge">Aufbewahrung im lokalen Papierkorb (A14); geräumt wird nach jedem periodischen Rescan.</param>
public sealed record SyncRunnerOptions(
    TimeSpan SettleDelay,
    TimeSpan LongPoll,
    TimeSpan RescanInterval,
    int Parallelism,
    TimeSpan MaxBackoff,
    TimeSpan? TrashMaxAge = null,
    long TrashMaxBytes = 10L * 1024 * 1024 * 1024)
{
    /// <summary>Meldet jeden Fehler (Pfad oder null für die ganze Aufgabe) – für Status in der Oberfläche und für Diagnose.</summary>
    public Action<RelativePath?, SyncError>? OnError { get; init; }

    public static SyncRunnerOptions Default { get; } =
        new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 4, TimeSpan.FromMinutes(1), TimeSpan.FromDays(30));
}

/// <summary>
/// Hält eine Sync-Aufgabe am Laufen: Start-Abgleich, dann parallel Watcher → <see cref="PathScheduler"/>, Feed-Long-Poll und periodischer bzw.
/// angeforderter Rescan. Vorübergehende Fehler führen zu Wiederholung mit wachsendem Abstand; ein <c>Fatal</c>-Fehler beendet den Lauf
/// mit <see cref="SyncStoppedException"/> (z. B. Gerät gesperrt), damit die Oberfläche es melden kann.
/// </summary>
public sealed class SyncRunner(
    SyncSession session,
    ILocalChangeSource changes,
    SyncRunnerOptions options,
    TimeProvider? clock = null,
    Status.ISyncObserver? observer = null)
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Channel<bool> _rescanRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private PathScheduler? _scheduler;

    public bool IsPaused => session.Gate.IsPaused;

    /// <summary>Hält die Aufgabe an: keine neue Arbeit, laufende Bewertungen enden normal (Tray-Menü „Pause“).</summary>
    public void Pause()
    {
        session.Gate.Pause();
        observer?.Paused(true);
    }

    public void Resume()
    {
        session.Gate.Resume();
        observer?.Paused(false);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await StartWithRetryAsync(ct);
        _scheduler = new PathScheduler(HandleAsync, options.Parallelism);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loops = new[] { WatchAsync(linked.Token), FeedAsync(linked.Token), RescanLoopAsync(linked.Token) };
        try
        {
            await await Task.WhenAny(loops); // der erste Abbruch (Fatal oder Aufrufer) beendet alle
        }
        finally
        {
            await linked.CancelAsync();
            await Task.WhenAll(loops.Select(l => l.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    /// <summary>Für Oberfläche und Tests: wartet, bis keine lokal ausgelösten Bewertungen mehr anstehen.</summary>
    public Task WhenLocalIdleAsync(CancellationToken ct) => _scheduler?.WhenIdleAsync(ct) ?? Task.CompletedTask;

    private async Task StartWithRetryAsync(CancellationToken ct)
    {
        var backoff = FirstBackoff;
        while (true)
        {
            var report = await session.StartAsync(startup: true, ct);
            Report(report.Failures);
            ThrowIfFatal(report.Failures.Select(f => f.Error));
            if (report.Failures.All(f => f.Error.Kind != SyncErrorKind.Transient))
            {
                return;
            }

            await Task.Delay(backoff, _clock, ct);
            backoff = Next(backoff);
        }
    }

    private async Task HandleAsync(RelativePath path, CancellationToken ct)
    {
        await Task.Delay(options.SettleDelay, _clock, ct);
        await session.Gate.WaitOpenAsync(ct);
        var result = await session.EvaluateLocalChangeAsync(path, ct);
        if (result.IsSuccess)
        {
            await FollowUpAsync(result.Value!, ct);
            return;
        }

        options.OnError?.Invoke(path, result.Error!);
        if (result.Error!.Kind == SyncErrorKind.Transient)
        {
            _ = RetryLaterAsync(path, options.MaxBackoff / 4, ct); // nicht blockieren; der Pfad kommt nach einer Pause wieder
        }
    }

    /// <summary>Folgearbeit eines Ergebnisses: Konfliktkopie bewerten; ein neu hochgeladener Ordner bringt seinen Inhalt mit (Watcher meldet oft nur den Ordner).</summary>
    private async Task FollowUpAsync(PathOutcome outcome, CancellationToken ct)
    {
        if (outcome.ConflictCopy is { } copy)
        {
            _scheduler!.Enqueue(copy);
        }

        if (outcome is { Kind: OutcomeKind.Uploaded, IsDirectory: true })
        {
            await session.RescanAsync(outcome.Path, startup: false, ct);
        }
    }

    private async Task MoveAsync(LocalChange.Moved move, CancellationToken ct)
    {
        // Sofort anmelden (noch vor dem ersten await): Feed, Rescan und weitere Ereignisse warten dann auf die Verschiebung.
        using var announced = session.Moves.Announce(move.From, move.To);

        try
        {
            await Task.Delay(options.SettleDelay, _clock, ct);
            await session.Gate.WaitOpenAsync(ct);
            var result = await session.ApplyLocalMoveAsync(move.From, move.To, evaluateAfter: true, ct);
            if (result.IsSuccess)
            {
                await FollowUpAsync(result.Value!, ct);
            }
            else if (Report(move.To, result.Error!).Kind == SyncErrorKind.Transient)
            {
                _scheduler!.Enqueue(move.From);
                _scheduler.Enqueue(move.To);
            }
        }
        catch (OperationCanceledException)
        {
            // Runner beendet
        }
    }

    private async Task RetryLaterAsync(RelativePath path, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _clock, ct);
            _scheduler!.Enqueue(path);
        }
        catch (OperationCanceledException)
        {
            // Runner beendet
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        await foreach (var change in changes.ReadAllAsync(ct))
        {
            switch (change)
            {
                case LocalChange.PathChanged changed:
                    _scheduler!.Enqueue(changed.Path);
                    break;
                case LocalChange.Moved moved:
                    _ = MoveAsync(moved, ct); // eigene Sperren je Pfadpaar; blockiert den Watcher nicht
                    break;
                case LocalChange.Overflow:
                    _rescanRequests.Writer.TryWrite(true);
                    break;
            }
        }
    }

    private async Task FeedAsync(CancellationToken ct)
    {
        var backoff = FirstBackoff;
        while (!ct.IsCancellationRequested)
        {
            await session.Gate.WaitOpenAsync(ct);
            var pulled = await session.PullChangesAsync(options.LongPoll, ct);
            if (pulled.IsSuccess)
            {
                backoff = FirstBackoff;
                continue;
            }

            options.OnError?.Invoke(null, pulled.Error!);
            ThrowIfFatal([pulled.Error!]);
            await Task.Delay(backoff, _clock, ct);
            backoff = Next(backoff);
        }
    }

    private async Task RescanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var interval = CancellationTokenSource.CreateLinkedTokenSource(ct);
            interval.CancelAfter(options.RescanInterval);
            try
            {
                await _rescanRequests.Reader.ReadAsync(interval.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Intervall abgelaufen: regulärer Rescan
            }

            await session.Gate.WaitOpenAsync(ct);
            var report = await session.RescanAsync(RelativePath.Root, startup: false, ct);
            Report(report.Failures);
            ThrowIfFatal(report.Failures.Select(f => f.Error));
            await session.CleanTrashAsync(options.TrashMaxAge ?? TimeSpan.FromDays(30), options.TrashMaxBytes, ct);
        }
    }

    private TimeSpan Next(TimeSpan backoff) => TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, options.MaxBackoff.Ticks));

    private void Report(IEnumerable<(RelativePath Path, SyncError Error)> failures)
    {
        foreach (var (path, error) in failures)
        {
            options.OnError?.Invoke(path, error);
        }
    }

    private SyncError Report(RelativePath path, SyncError error)
    {
        options.OnError?.Invoke(path, error);
        return error;
    }

    private void ThrowIfFatal(IEnumerable<SyncError> errors)
    {
        if (errors.FirstOrDefault(e => e.Kind == SyncErrorKind.Fatal) is { } fatal)
        {
            observer?.Stopped(fatal);
            throw new SyncStoppedException(fatal);
        }
    }
}

/// <summary>Die Sync-Aufgabe kann nicht weiterlaufen (z. B. Anmeldung entzogen, Space gelöscht).</summary>
public sealed class SyncStoppedException(SyncError error) : Exception($"{error.Code}: {error.Message}")
{
    public SyncError Error { get; } = error;
}
