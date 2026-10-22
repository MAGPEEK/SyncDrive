using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Control;

/// <summary>Was eine laufende Aufgabe braucht. <see cref="Resources"/> gibt der Überwacher frei, wenn der Lauf endet (Watcher, Datenbanken).</summary>
public sealed record TaskComponents(
    SyncSession Session,
    ILocalChangeSource Changes,
    IActivityLog Log,
    TaskStatusTracker Tracker,
    IAsyncDisposable? Resources = null);

/// <summary>
/// Baut die Bestandteile einer Aufgabe (Ordner, Basis, Server-Verbindung). Der Daemon setzt sie aus den Infra-Adaptern zusammen, Tests aus
/// Fakes. Fehlt die Anmeldung, wirft die Fabrik <see cref="SyncStoppedException"/>.
/// </summary>
public interface ITaskComponentsFactory
{
    Task<TaskComponents> CreateAsync(SyncTaskDefinition task, CancellationToken ct);
}

/// <summary>Aufgaben zur Laufzeit starten und beenden (Assistent legt an, „Delete“ entfernt).</summary>
public interface ITaskLifecycle
{
    void Start(SyncTaskDefinition task);

    /// <summary>Beendet den Lauf und nimmt die Aufgabe aus der Statusliste; lokale Dateien bleiben.</summary>
    Task StopAsync(string taskId);
}

/// <summary>Namen der Geheimnisse im <see cref="ICredentialStore"/>.</summary>
public static class CredentialKeys
{
    public static string DeviceToken(string connectionId) => $"connection/{connectionId}/device-token";

    public static string ProxyPassword => "proxy/password";
}

/// <summary>Fehlercodes, die der Daemon selbst vergibt (nicht vom Server).</summary>
public static class DaemonErrors
{
    /// <summary>Kein Geräte-Token für die Verbindung: Nutzer muss sich neu anmelden.</summary>
    public const string NotSignedIn = "not_signed_in";

    /// <summary>Verbindung der Aufgabe existiert nicht mehr.</summary>
    public const string UnknownConnection = "unknown_connection";

    /// <summary>Der lokale Ordner der Aufgabe fehlt (gelöscht, Laufwerk nicht da).</summary>
    public const string LocalFolderMissing = "local_folder_missing";

    /// <summary>On-Demand ist für diesen Ordner nicht möglich (kein Windows ab 1809, kein NTFS, Registrierung abgelehnt).</summary>
    public const string OnDemandUnavailable = "on_demand_unavailable";
}

/// <param name="FirstRestartDelay">Wartezeit vor dem ersten Neustart nach einem unerwarteten Fehler; verdoppelt sich bis <paramref name="MaxRestartDelay"/>.</param>
public sealed record SupervisorOptions(SyncRunnerOptions Runner, TimeSpan FirstRestartDelay, TimeSpan MaxRestartDelay)
{
    public static SupervisorOptions Default { get; } = new(SyncRunnerOptions.Default, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

    /// <summary>Meldet unerwartete Fehler (Absturz eines Laufs) für das Daemon-Log.</summary>
    public Action<string, Exception>? OnCrash { get; init; }
}

/// <summary>
/// Hält alle Aufgaben am Laufen: je Aufgabe ein <see cref="SyncRunner"/>, angemeldet beim <see cref="DaemonController"/>. Ein unerwarteter
/// Fehler startet den Lauf nach einer Pause neu (Bestandteile werden neu gebaut). Ein <c>Fatal</c>-Fehler (Gerät gesperrt, Space weg,
/// Anmeldung fehlt) beendet die Aufgabe; sie bleibt mit Zustand „deaktiviert“ und Grund in der Liste, bis der Nutzer handelt.
/// </summary>
public sealed class TaskSupervisor(DaemonController controller, ITaskComponentsFactory factory, SupervisorOptions options, TimeProvider? clock = null)
    : ITaskLifecycle
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Stop, Task Run)> _running = new(StringComparer.Ordinal);
    private CancellationToken _lifetime;

    /// <summary>Startet die gespeicherten Aufgaben und läuft, bis <paramref name="ct"/> abbricht; später angelegte Aufgaben kommen über <see cref="Start"/>.</summary>
    public async Task RunAsync(IReadOnlyList<SyncTaskDefinition> tasks, CancellationToken ct)
    {
        _lifetime = ct;
        foreach (var task in tasks)
        {
            Start(task);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // Daemon endet
        }

        Task[] runs;
        lock (_gate)
        {
            runs = [.. _running.Values.Select(r => r.Run)];
        }

        await Task.WhenAll(runs);
    }

    public void Start(SyncTaskDefinition task)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(task.Id))
            {
                return;
            }

            var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            _running[task.Id] = (stop, RunTaskAsync(task, stop.Token));
        }
    }

    public async Task StopAsync(string taskId)
    {
        (CancellationTokenSource Stop, Task Run) entry;
        lock (_gate)
        {
            if (!_running.Remove(taskId, out entry))
            {
                controller.Unregister(taskId);
                return;
            }
        }

        await entry.Stop.CancelAsync();
        await entry.Run;
        entry.Stop.Dispose();
        controller.Unregister(taskId);
    }

    private async Task RunTaskAsync(SyncTaskDefinition task, CancellationToken ct)
    {
        var delay = options.FirstRestartDelay;
        while (!ct.IsCancellationRequested)
        {
            TaskComponents? components = null;
            try
            {
                components = await factory.CreateAsync(task, ct);
                var runner = new SyncRunner(components.Session, components.Changes, options.Runner, _clock, components.Tracker);
                if (task.Paused)
                {
                    runner.Pause();
                }

                controller.Unregister(task.Id);
                controller.Register(task.Id, runner, components.Tracker, components.Log);
                await runner.RunAsync(ct);
                return;
            }
            catch (SyncStoppedException stopped)
            {
                // Bleibt sichtbar; ein Tracker aus dem Lauf hat den Grund schon, sonst (Fabrik scheiterte) einen eigenen anlegen.
                if (components is null)
                {
                    var tracker = new TaskStatusTracker(task.Id, _clock);
                    tracker.Stopped(stopped.Error);
                    controller.Unregister(task.Id);
                    controller.Register(task.Id, null, tracker, new NullActivityLog());
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception crash)
            {
                options.OnCrash?.Invoke(task.Id, crash);
                components?.Tracker.PathFinished(
                    Core.Paths.RelativePath.Root, Result.Fail<PathOutcome>(SyncErrorKind.Transient, "crashed", crash.Message));
            }
            finally
            {
                if (components?.Resources is { } resources)
                {
                    await resources.DisposeAsync();
                }
            }

            try
            {
                await Task.Delay(delay, _clock, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.MaxRestartDelay.Ticks));
        }
    }
}
