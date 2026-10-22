using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Core.Results;
using SyncDrive.Infra.Fs;
using SyncDrive.Infra.Http;
using SyncDrive.Infra.Sqlite.Client;

namespace SyncDrive.Client.Daemon;

/// <summary>Ablage des Clients im Benutzerprofil: Konfiguration, Geheimnisse, je Aufgabe eine Datenbank (Basis und Verlauf), Logs.</summary>
public sealed record ClientPaths(string Root)
{
    /// <summary>
    /// Unter Windows <c>%LOCALAPPDATA%\SyncDrive</c> (lokales Profil, wandert nicht mit). <c>SYNCDRIVE_CLIENT_HOME</c> lenkt den
    /// Ordner um (portable Installation, Tests).
    /// </summary>
    public static ClientPaths Default() =>
        new(Environment.GetEnvironmentVariable("SYNCDRIVE_CLIENT_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncDrive"));

    public string ConfigDatabase => Path.Combine(Root, "client.db");

    public string Secrets => Path.Combine(Root, "secrets");

    public string Logs => Path.Combine(Root, "logs");

    public string TaskDatabase(string taskId) => Path.Combine(Root, "tasks", taskId + ".db");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "tasks"));
        Directory.CreateDirectory(Logs);
    }
}

/// <summary>Setzt eine Aufgabe aus den echten Adaptern zusammen: lokaler Ordner, Watcher, SQLite, HTTPS mit gemerktem Zertifikat, Unterordner.</summary>
/// <param name="remoteFactory">Ersetzt in Tests die HTTPS-Verbindung (z. B. durch einen In-Process-Server).</param>
/// <param name="cloudFolders">Sync-Root der Plattform (Statussymbole, On-Demand); ohne ihn gibt es keine Symbole und kein On-Demand.</param>
public sealed class TaskComponentsFactory(
    ClientPaths paths,
    IClientConfigStore config,
    ICredentialStore credentials,
    string deviceName,
    Func<HttpRemoteOptions, IRemoteApi>? remoteFactory = null,
    ICloudFolders? cloudFolders = null,
    Action<string>? log = null) : ITaskComponentsFactory
{
    public async Task<TaskComponents> CreateAsync(SyncTaskDefinition task, CancellationToken ct)
    {
        var connection = (await config.ListConnectionsAsync(ct)).FirstOrDefault(c => c.Id == task.ConnectionId)
            ?? throw Stop(DaemonErrors.UnknownConnection, "Die Verbindung dieser Aufgabe gibt es nicht mehr.");
        var token = await credentials.GetAsync(CredentialKeys.DeviceToken(connection.Id), ct)
            ?? throw Stop(DaemonErrors.NotSignedIn, $"Bitte erneut bei {connection.DisplayName} anmelden.");
        if (!Directory.Exists(task.LocalPath))
        {
            throw Stop(DaemonErrors.LocalFolderMissing, $"Der lokale Ordner {task.LocalPath} fehlt.");
        }

        var settings = await config.GetSettingsAsync(ct);
        var proxy = await ClientSetup.ProxyAsync(config, credentials, ct);
        var options = new HttpRemoteOptions(connection.ServerUrl, token, task.SpaceId, connection.PinnedCertificateSha256, proxy);
        var remote = SubtreeRemoteApi.For(remoteFactory?.Invoke(options) ?? HttpRemoteApi.Create(options), task.RemotePath);

        var database = paths.TaskDatabase(task.Id);
        var state = new SqliteSessionState(database);
        var activity = new SqliteActivityLog(database);
        var local = new LocalFileStore(task.LocalPath);
        var cloud = await AttachCloudAsync(task, remote, ct);
        var engine = new SyncEngine(
            local, remote, state, SyncTaskConfig.From(task, settings, deviceName), activity, TimeProvider.System, cloud?.Placeholders);
        var tracker = new TaskStatusTracker(task.Id);
        ISyncObserver observer = cloud is null
            ? tracker
            : new ObserverGroup(tracker, new PlaceholderStatusObserver(cloud.Placeholders, (path, e) => log?.Invoke($"Symbol {path}: {e.Message}")));
        var session = new SyncSession(engine, local, remote, state, observer);
        var changes = new FileSystemChangeSource(task.LocalPath);
        return new TaskComponents(session, changes, activity, tracker, new DisposeAction(async () =>
        {
            changes.Dispose();
            if (cloud is not null)
            {
                await cloud.Connection.DisposeAsync();
            }
        }));
    }

    /// <summary>
    /// Verbindet den Ordner als Sync-Root. Scheitert das, fehlen nur die Symbole; eine On-Demand-Aufgabe kann ohne Platzhalter aber nicht laufen
    /// (sie würde sonst alles herunterladen, und das hat der Nutzer abgewählt).
    /// </summary>
    private async Task<CloudFolder?> AttachCloudAsync(SyncTaskDefinition task, IRemoteApi remote, CancellationToken ct)
    {
        if (cloudFolders is null)
        {
            return task.OnDemand ? throw Stop(DaemonErrors.OnDemandUnavailable, "On-Demand-Sync ist auf diesem System nicht verfügbar.") : null;
        }

        try
        {
            return await cloudFolders.AttachAsync(task, new Hydrator(remote), ct);
        }
        catch (IOException e)
        {
            log?.Invoke($"Sync-Root {task.LocalPath}: {e.Message}");
            return task.OnDemand ? throw Stop(DaemonErrors.OnDemandUnavailable, $"On-Demand-Sync für {task.LocalPath} nicht möglich: {e.Message}") : null;
        }
    }

    private static SyncStoppedException Stop(string code, string message) => new(new SyncError(SyncErrorKind.Fatal, code, message));

    private sealed class DisposeAction(Func<Task> dispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dispose();
    }
}
