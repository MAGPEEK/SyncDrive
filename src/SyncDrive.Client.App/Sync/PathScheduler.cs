using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Sync;

/// <summary>
/// Warteschlange „Pfad neu bewerten“ (ADR-2). Regeln:
/// <list type="bullet">
/// <item>Mehrfaches Einreihen desselben Pfads wird zusammengefasst (Groß-/Kleinschreibung egal).</item>
/// <item>Derselbe Pfad läuft nie parallel; verschiedene Pfade laufen bis <c>maxParallelism</c> gleichzeitig.</item>
/// <item>Kommt für einen laufenden Pfad ein neues Ereignis, wird der Lauf abgebrochen und danach erneut gestartet
///   (bricht die Arbeit am Pfad ab).</item>
/// </list>
/// </summary>
public sealed class PathScheduler
{
    private readonly Func<RelativePath, CancellationToken, Task> _handler;
    private readonly Action<RelativePath, Exception> _onError;
    private readonly int _maxParallelism;
    private readonly Lock _gate = new();
    private readonly Queue<string> _ready = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _running;
    private TaskCompletionSource _idle = NewCompletedSignal();

    public PathScheduler(
        Func<RelativePath, CancellationToken, Task> handler,
        int maxParallelism,
        Action<RelativePath, Exception>? onError = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelism, 1);
        _handler = handler;
        _maxParallelism = maxParallelism;
        _onError = onError ?? ((_, _) => { });
    }

    /// <summary>Pfade, die warten oder gerade laufen.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Enqueue(RelativePath path)
    {
        lock (_gate)
        {
            if (_idle.Task.IsCompleted)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (_entries.TryGetValue(path.Value, out var entry))
            {
                entry.Path = path;
                if (entry.Running is { } running)
                {
                    entry.Dirty = true;
                    running.Cancel();
                }

                return;
            }

            _entries[path.Value] = new Entry(path);
            _ready.Enqueue(path.Value);
            StartWorkersLocked();
        }
    }

    /// <summary>Wartet, bis nichts mehr wartet oder läuft.</summary>
    public Task WhenIdleAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return _idle.Task.WaitAsync(ct);
        }
    }

    private static TaskCompletionSource NewCompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private void StartWorkersLocked()
    {
        while (_running < _maxParallelism && _ready.Count > 0)
        {
            _running++;
            _ = Task.Run(WorkAsync);
        }
    }

    private async Task WorkAsync()
    {
        while (TakeNext() is { } job)
        {
            try
            {
                await _handler(job.Path, job.Cancellation.Token);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                // Abgebrochen wegen eines neuen Ereignisses; der Pfad läuft gleich erneut.
            }
            catch (Exception e)
            {
                _onError(job.Path, e);
            }
            finally
            {
                Finish(job);
            }
        }
    }

    private Job? TakeNext()
    {
        lock (_gate)
        {
            if (_ready.Count == 0)
            {
                _running--;
                if (_running == 0 && _entries.Count == 0)
                {
                    _idle.TrySetResult();
                }

                return null;
            }

            var entry = _entries[_ready.Dequeue()];
            entry.Dirty = false;
            entry.Running = new CancellationTokenSource();
            return new Job(entry.Path, entry.Running);
        }
    }

    private void Finish(Job job)
    {
        lock (_gate)
        {
            var key = job.Path.Value;
            var entry = _entries[key];
            entry.Running = null;
            job.Cancellation.Dispose();
            if (entry.Dirty)
            {
                _ready.Enqueue(key);
            }
            else
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Entry(RelativePath path)
    {
        public RelativePath Path { get; set; } = path;

        public bool Dirty { get; set; }

        public CancellationTokenSource? Running { get; set; }
    }

    private sealed record Job(RelativePath Path, CancellationTokenSource Cancellation);
}
