using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Sync;

/// <summary>
/// Lokale Verschiebungen, die der Watcher gemeldet hat, die aber noch nicht zum Server übertragen sind. Solange eine läuft, darf kein anderer
/// Weg (Feed, Rescan, weitere Watcher-Ereignisse) Quelle oder Ziel bewerten: Der fehlende Quellordner sähe sonst wie eine Löschung aus,
/// und aus der Umbenennung würde „löschen + neu hochladen“.
/// </summary>
public sealed class PendingMoves
{
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];

    /// <summary>Meldet eine Verschiebung an; sie gilt, bis <see cref="Complete"/> sie abschließt oder das Ergebnis freigegeben wird.</summary>
    public IDisposable Announce(RelativePath from, RelativePath to)
    {
        var entry = new Entry(from, to);
        lock (_lock)
        {
            _entries.Add(entry);
        }

        return new Release(this, entry);
    }

    /// <summary>Die Verschiebung ist übertragen (oder verworfen); wartende Bewertungen dürfen weiter.</summary>
    public void Complete(RelativePath from, RelativePath to)
    {
        List<Entry> done;
        lock (_lock)
        {
            done = [.. _entries.Where(e => e.From.EqualsIgnoreCase(from) && e.To.EqualsIgnoreCase(to))];
            _entries.RemoveAll(done.Contains);
        }

        done.ForEach(e => e.Done.TrySetResult());
    }

    /// <summary>Wartet, bis keine angemeldete Verschiebung mehr <paramref name="path"/> berührt (als Quelle, Ziel oder darunter).</summary>
    public async Task WaitClearAsync(RelativePath path, CancellationToken ct)
    {
        while (true)
        {
            Task[] blocking;
            lock (_lock)
            {
                blocking = [.. _entries.Where(e => Touches(e, path)).Select(e => e.Done.Task)];
            }

            if (blocking.Length == 0)
            {
                return;
            }

            await Task.WhenAll(blocking).WaitAsync(ct);
        }
    }

    private static bool Touches(Entry entry, RelativePath path) =>
        path.IsSameOrDescendantOf(entry.From) || path.IsSameOrDescendantOf(entry.To)
        || entry.From.IsSameOrDescendantOf(path) || entry.To.IsSameOrDescendantOf(path);

    private void Remove(Entry entry)
    {
        lock (_lock)
        {
            _entries.Remove(entry);
        }

        entry.Done.TrySetResult();
    }

    private sealed record Entry(RelativePath From, RelativePath To)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Release(PendingMoves owner, Entry entry) : IDisposable
    {
        public void Dispose() => owner.Remove(entry);
    }
}
