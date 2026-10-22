using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Status;

/// <summary>Gesamtzustand für Tray-Symbol und Panel-Kopf (7 Symbolsätze).</summary>
public enum TrayState
{
    /// <summary>Keine Aufgabe eingerichtet.</summary>
    Normal,

    UpToDate,

    Syncing,

    Paused,

    /// <summary>Elemente konnten nicht synchronisiert werden („abnormal“).</summary>
    Error,

    Disconnected,
}

/// <summary>Zustand einer Aufgabe (Ordnersymbol in der Aufgabenliste: connecting, disable, done, error, pause, sync).</summary>
public enum TaskState
{
    Connecting,
    Disabled,
    Done,
    Error,
    Paused,
    Syncing,
}

/// <param name="Pending">Pfade, die gerade bewertet oder übertragen werden.</param>
/// <param name="FailingPaths">Pfade, deren letzter Versuch dauerhaft fehlschlug (bis ein späterer gelingt).</param>
public sealed record TaskStatus(
    string TaskId,
    TaskState State,
    int Pending,
    string? CurrentPath,
    IReadOnlyList<string> FailingPaths,
    string? LastError,
    DateTimeOffset? LastSyncedAt);

public sealed record OverallStatus(TrayState Tray, bool HasNotifications, int Pending, IReadOnlyList<TaskStatus> Tasks)
{
    /// <summary>
    /// Verdichtet die Aufgaben zu einem Tray-Zustand. Reihenfolge: keine Aufgaben ⇒ Normal; alle angehalten ⇒ Paused; ein Fehler ⇒ Error;
    /// keine Verbindung ⇒ Disconnected; Arbeit ⇒ Syncing; sonst UpToDate. Fehler gehen vor, weil der Nutzer handeln muss.
    /// </summary>
    public static OverallStatus Combine(IReadOnlyList<TaskStatus> tasks, bool hasNotifications)
    {
        var tray = tasks.Count == 0 ? TrayState.Normal
            : tasks.All(t => t.State is TaskState.Paused or TaskState.Disabled) ? TrayState.Paused
            : tasks.Any(t => t.State == TaskState.Error) ? TrayState.Error
            : tasks.Any(t => t.State == TaskState.Connecting) ? TrayState.Disconnected
            : tasks.Any(t => t.State == TaskState.Syncing) ? TrayState.Syncing
            : TrayState.UpToDate;
        return new OverallStatus(tray, hasNotifications, tasks.Sum(t => t.Pending), tasks);
    }
}

/// <summary>Beobachter einer Sync-Sitzung; die Sitzung meldet Beginn und Ende jeder Bewertung, der Runner Verbindungs- und Pausenzustand.</summary>
public interface ISyncObserver
{
    void PathStarted(RelativePath path);

    void PathFinished(RelativePath path, Result<PathOutcome> result);

    void Connected();

    void Paused(bool paused);

    void Stopped(SyncError reason);
}

/// <summary>Leitet aus den Ereignissen einer Aufgabe ihren <see cref="TaskStatus"/> ab und meldet Änderungen. Threadsicher.</summary>
public sealed class TaskStatusTracker(string taskId, TimeProvider? clock = null) : ISyncObserver
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);
    private bool _offline = true;
    private bool _paused;
    private SyncError? _stopped;
    private string? _lastError;
    private DateTimeOffset? _lastSynced;

    public event Action<TaskStatus>? Changed;

    public TaskStatus Snapshot
    {
        get
        {
            lock (_gate)
            {
                var state = _stopped is not null ? TaskState.Disabled
                    : _paused ? TaskState.Paused
                    : _offline ? TaskState.Connecting
                    : _active.Count > 0 ? TaskState.Syncing
                    : _failing.Count > 0 ? TaskState.Error
                    : TaskState.Done;
                return new TaskStatus(taskId, state, _active.Count, _active.Keys.FirstOrDefault(), [.. _failing], _stopped?.Message ?? _lastError, _lastSynced);
            }
        }
    }

    public void PathStarted(RelativePath path) => Update(() => _active[path.Value] = _active.GetValueOrDefault(path.Value) + 1);

    public void PathFinished(RelativePath path, Result<PathOutcome> result) => Update(() =>
    {
        if (_active.TryGetValue(path.Value, out var count) && count > 1)
        {
            _active[path.Value] = count - 1;
        }
        else
        {
            _active.Remove(path.Value);
        }

        if (result.IsSuccess)
        {
            _offline = false;
            _failing.Remove(path.Value);
            _lastSynced = _clock.GetUtcNow();
            return;
        }

        var error = result.Error!;
        _lastError = error.Message;
        if (error.Code == RemoteErrors.Network)
        {
            _offline = true;
        }
        else if (error.Kind == SyncErrorKind.Permanent)
        {
            _offline = false;
            _failing.Add(path.Value);
        }
    });

    public void Connected() => Update(() => _offline = false);

    public void Paused(bool paused) => Update(() => _paused = paused);

    public void Stopped(SyncError reason) => Update(() => _stopped = reason);

    private void Update(Action change)
    {
        TaskStatus snapshot;
        lock (_gate)
        {
            change();
        }

        snapshot = Snapshot;
        Changed?.Invoke(snapshot);
    }
}
