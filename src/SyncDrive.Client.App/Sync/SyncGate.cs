namespace SyncDrive.Client.App.Sync;

/// <summary>
/// Pause/Fortsetzen an einer Stelle statt verstreuter Flags (01-architektur.md, Nebenläufigkeit). Während der Pause beginnt keine neue
/// Arbeit; laufende Bewertungen dürfen zu Ende gehen.
/// </summary>
public sealed class SyncGate
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _open = Opened();

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return !_open.Task.IsCompleted;
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_open.Task.IsCompleted)
            {
                _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _open.TrySetResult();
        }
    }

    public Task WaitOpenAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return _open.Task.WaitAsync(ct);
        }
    }

    private static TaskCompletionSource Opened()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }
}
