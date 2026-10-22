namespace SyncDrive.Client.App.Sync;

/// <summary>
/// Asynchrone Sperre je Pfad (Groß-/Kleinschreibung egal). Feed, Watcher und Rescan bewerten parallel, aber nie denselben Pfad gleichzeitig.
/// Sperren werden freigegeben, sobald niemand mehr wartet.
/// </summary>
public sealed class PathLocks
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public async Task<IDisposable> AcquireAsync(string path, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out entry!))
            {
                _entries[path] = entry = new Entry();
            }

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Release(path, entry, held: false);
            throw;
        }

        return new Lease(() => Release(path, entry, held: true));
    }

    private void Release(string path, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (_gate)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(path);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }
}
