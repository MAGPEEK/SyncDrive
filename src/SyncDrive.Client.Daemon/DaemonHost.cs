using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Infra.Http;
using SyncDrive.Infra.Ipc;

namespace SyncDrive.Client.Daemon;

/// <summary>
/// Der Daemon ohne Prozess-Drumherum: liest die Aufgaben, hält sie über den <see cref="TaskSupervisor"/> am Laufen und bedient die
/// Oberfläche über die Pipe (Status und Einrichtung). Endet, wenn <c>ct</c> abbricht.
/// </summary>
public sealed class DaemonHost(
    ClientPaths paths,
    IClientConfigStore config,
    ICredentialStore credentials,
    string deviceName,
    string pipeName,
    SupervisorOptions options,
    Func<HttpRemoteOptions, IRemoteApi>? remoteFactory = null,
    IServerAccess? serverAccess = null,
    IAutostart? autostart = null,
    Func<bool, TaskEnvironment>? environment = null,
    TimeSpan? notificationInterval = null,
    IShellIntegration? shellIntegration = null,
    ICloudFolders? cloudFolders = null,
    Action<string>? log = null)
{
    public DaemonController Controller { get; } = new();

    public async Task RunAsync(CancellationToken ct)
    {
        paths.EnsureCreated();
        ApplyAutostart((await config.GetSettingsAsync(ct)).StartWithWindows);
        var tasks = await config.ListTasksAsync(ct);
        var factory = new TaskComponentsFactory(paths, config, credentials, deviceName, remoteFactory, cloudFolders, log);
        var supervisor = new TaskSupervisor(Controller, factory, options);
        var server = serverAccess ?? new HttpServerAccess(proxy: c => ClientSetup.ProxyAsync(config, credentials, c));
        using var notifications = new NotificationCenter(config, credentials, server, Controller, notificationInterval);
        var setup = new ClientSetup(
            config, credentials, server, supervisor, environment ?? LocalEnvironment.For, deviceName, autostart, notifications, shellIntegration,
            cloudFolders);
        var shell = new ShellService(config, credentials, server, Controller, setup, cloudFolders);
        var ipc = new IpcServer(pipeName, Controller, setup, shell);
        if (shellIntegration is not null)
        {
            await shellIntegration.RefreshAsync(ct); // Menü passend zu den gespeicherten Aufgaben
        }

        await Task.WhenAll(supervisor.RunAsync(tasks, ct), ipc.RunAsync(ct), notifications.RunAsync(ct));
    }

    /// <summary>
    /// „Mit Windows starten“ gilt ab Werk: Der Eintrag entsteht beim Start, nicht erst beim ersten Speichern der Einstellungen, und zeigt nach
    /// einem Update wieder auf das installierte Programm.
    /// </summary>
    private void ApplyAutostart(bool enabled)
    {
        try
        {
            autostart?.Apply(enabled);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log?.Invoke($"Autostart: {e.Message}");
        }
    }
}
