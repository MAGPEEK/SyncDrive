namespace SyncDrive.Server.App.Spaces;

/// <summary>Weckt wartende Long-Poll-Anfragen, sobald ein Space neue Änderungen hat.</summary>
public interface IChangeNotifier
{
    /// <summary>Zählt bei jeder Änderung hoch; vor dem Lesen des Feeds zu merken, damit kein Wecken verloren geht.</summary>
    long CurrentVersion(string spaceId);

    void Notify(string spaceId);

    /// <summary>Wartet, bis sich die Version von <paramref name="seenVersion"/> unterscheidet (oder abgebrochen wird).</summary>
    Task WaitAsync(string spaceId, long seenVersion, CancellationToken ct);
}

/// <summary>Benachrichtigung innerhalb eines Prozesses; genügt, solange ein einzelner Server-Prozess läuft.</summary>
public sealed class InProcessChangeNotifier : IChangeNotifier
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Signal> _signals = [];

    public long CurrentVersion(string spaceId)
    {
        lock (_gate)
        {
            return SignalFor(spaceId).Version;
        }
    }

    public void Notify(string spaceId)
    {
        TaskCompletionSource toComplete;
        lock (_gate)
        {
            var signal = SignalFor(spaceId);
            signal.Version++;
            toComplete = signal.Next;
            signal.Next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        toComplete.SetResult();
    }

    public Task WaitAsync(string spaceId, long seenVersion, CancellationToken ct)
    {
        Task pending;
        lock (_gate)
        {
            var signal = SignalFor(spaceId);
            if (signal.Version != seenVersion)
            {
                return Task.CompletedTask;
            }

            pending = signal.Next.Task;
        }

        return pending.WaitAsync(ct);
    }

    private Signal SignalFor(string spaceId)
    {
        if (!_signals.TryGetValue(spaceId, out var signal))
        {
            _signals[spaceId] = signal = new Signal();
        }

        return signal;
    }

    private sealed class Signal
    {
        public long Version { get; set; }

        public TaskCompletionSource Next { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Tut nichts; für Tests und Aufrufer ohne Long-Poll. Warten endet erst durch Abbruch.</summary>
public sealed class NullChangeNotifier : IChangeNotifier
{
    public long CurrentVersion(string spaceId) => 0;

    public void Notify(string spaceId)
    {
    }

    public Task WaitAsync(string spaceId, long seenVersion, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
}
