using System.Threading.Channels;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;

namespace SyncDrive.Client.App.Control;

/// <summary>Verlaufseintrag mit der Aufgabe, zu der er gehört (Panel „Recently Changed“ zeigt den Ordner der Aufgabe).</summary>
public sealed record TaskActivity(string TaskId, ActivityEntry Entry);

/// <summary>
/// Was die Oberfläche vom Daemon braucht. Der Daemon setzt es mit <see cref="DaemonController"/> um, die Oberfläche spricht es über IPC an;
/// beide Seiten sehen dieselbe Schnittstelle.
/// </summary>
public interface IDaemonControl
{
    Task<OverallStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Neueste zuerst, über alle Aufgaben.</summary>
    Task<IReadOnlyList<TaskActivity>> GetRecentActivityAsync(int limit, CancellationToken ct);

    /// <summary>Hält eine Aufgabe an, ohne <paramref name="taskId"/> alle (Tray-Menü „Pause“).</summary>
    Task PauseAsync(string? taskId, CancellationToken ct);

    Task ResumeAsync(string? taskId, CancellationToken ct);

    /// <summary>Liefert sofort den aktuellen Stand und danach jeden neuen. Zwischenstände dürfen entfallen, der letzte kommt immer an.</summary>
    IAsyncEnumerable<OverallStatus> WatchStatusAsync(CancellationToken ct);
}

/// <summary>Die Aufgabe ist dem Daemon nicht bekannt.</summary>
public sealed class UnknownTaskException(string taskId) : Exception($"Unbekannte Aufgabe: {taskId}")
{
    public string TaskId { get; } = taskId;
}

/// <summary>Daemon-Seite von <see cref="IDaemonControl"/>: hält die laufenden Aufgaben mit Runner, Status und Verlauf.</summary>
public sealed class DaemonController : IDaemonControl
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Registration> _tasks = new(StringComparer.Ordinal);
    private readonly List<Channel<OverallStatus>> _watchers = [];
    private bool _hasNotifications;

    /// <summary>Ungelesene Benachrichtigungen (Badge am Tray-Symbol).</summary>
    public bool HasNotifications
    {
        get
        {
            lock (_gate)
            {
                return _hasNotifications;
            }
        }

        set
        {
            lock (_gate)
            {
                _hasNotifications = value;
            }

            Publish();
        }
    }

    /// <param name="runner">Null für eine Aufgabe, die nicht laufen kann (z. B. Anmeldung fehlt); sie erscheint trotzdem mit Zustand und Grund.</param>
    public void Register(string taskId, SyncRunner? runner, TaskStatusTracker tracker, IActivityLog log)
    {
        var registration = new Registration(runner, tracker, log, _ => Publish());
        lock (_gate)
        {
            if (!_tasks.TryAdd(taskId, registration))
            {
                throw new InvalidOperationException($"Aufgabe {taskId} ist schon registriert.");
            }
        }

        tracker.Changed += registration.OnChanged;
        Publish();
    }

    public void Unregister(string taskId)
    {
        Registration? removed;
        lock (_gate)
        {
            _tasks.Remove(taskId, out removed);
        }

        if (removed is not null)
        {
            removed.Tracker.Changed -= removed.OnChanged;
            Publish();
        }
    }

    public Task<OverallStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(Current());

    /// <summary>Stand einer Aufgabe (Dateistatus im Explorer); null, wenn sie nicht läuft.</summary>
    public Status.TaskStatus? StatusOf(string taskId)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(taskId, out var registration) ? registration.Tracker.Snapshot : null;
        }
    }

    public async Task<IReadOnlyList<TaskActivity>> GetRecentActivityAsync(int limit, CancellationToken ct)
    {
        var all = new List<TaskActivity>();
        foreach (var (taskId, registration) in Snapshot())
        {
            var entries = await registration.Log.RecentAsync(limit, ct);
            all.AddRange(entries.Select(e => new TaskActivity(taskId, e)));
        }

        return [.. all.OrderByDescending(a => a.Entry.At).Take(limit)];
    }

    public Task PauseAsync(string? taskId, CancellationToken ct)
    {
        foreach (var registration in Select(taskId))
        {
            registration.Runner?.Pause();
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(string? taskId, CancellationToken ct)
    {
        foreach (var registration in Select(taskId))
        {
            registration.Runner?.Resume();
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<OverallStatus> WatchStatusAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<OverallStatus>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            _watchers.Add(channel);
        }

        try
        {
            channel.Writer.TryWrite(Current());
            await foreach (var status in channel.Reader.ReadAllAsync(ct))
            {
                yield return status;
            }
        }
        finally
        {
            lock (_gate)
            {
                _watchers.Remove(channel);
            }
        }
    }

    private OverallStatus Current()
    {
        bool notifications;
        List<(string, Registration)> tasks;
        lock (_gate)
        {
            notifications = _hasNotifications;
            tasks = [.. _tasks.Select(t => (t.Key, t.Value))];
        }

        return OverallStatus.Combine([.. tasks.OrderBy(t => t.Item1, StringComparer.Ordinal).Select(t => t.Item2.Tracker.Snapshot)], notifications);
    }

    private void Publish()
    {
        var status = Current();
        Channel<OverallStatus>[] watchers;
        lock (_gate)
        {
            watchers = [.. _watchers];
        }

        foreach (var watcher in watchers)
        {
            watcher.Writer.TryWrite(status);
        }
    }

    private List<(string TaskId, Registration Registration)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _tasks.Select(t => (t.Key, t.Value))];
        }
    }

    private List<Registration> Select(string? taskId)
    {
        lock (_gate)
        {
            if (taskId is null)
            {
                return [.. _tasks.Values];
            }

            return _tasks.TryGetValue(taskId, out var registration) ? [registration] : throw new UnknownTaskException(taskId);
        }
    }

    private sealed record Registration(SyncRunner? Runner, TaskStatusTracker Tracker, IActivityLog Log, Action<Status.TaskStatus> OnChanged);
}
