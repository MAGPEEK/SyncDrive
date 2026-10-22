using System.Security.Cryptography;
using System.Text;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.Daemon;
using SyncDrive.Infra.CloudFiles;
using SyncDrive.Infra.Ipc;
using SyncDrive.Infra.Secrets;
using SyncDrive.Infra.Sqlite.Client;

// Daemon des SyncDrive-Clients: ein Prozess je angemeldetem Benutzer (ADR-8a). Die Oberfläche startet ihn bei Bedarf und spricht
// über die Pipe mit ihm; er läuft weiter, wenn die Oberfläche geschlossen wird.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Der SyncDrive-Client läuft derzeit nur unter Windows.");
    return 2;
}

var paths = ClientPaths.Default();
paths.EnsureCreated();
var userKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName.ToUpperInvariant())))[..16];
using var single = new Mutex(initiallyOwned: true, $@"Local\SyncDrive.Daemon.{userKey}", out var first);
if (!first)
{
    return 0; // läuft schon; die Oberfläche verbindet sich mit dem vorhandenen Daemon
}

using var log = new StreamWriter(Path.Combine(paths.Logs, "daemon.log"), append: true) { AutoFlush = true };
var logGate = new Lock();
void Log(string message)
{
    lock (logGate)
    {
        log.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
    }
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

var options = SupervisorOptions.Default with { OnCrash = (task, e) => Log($"Aufgabe {task} abgestürzt, Neustart folgt: {e}") };
var host = new DaemonHost(
    paths,
    new SqliteClientConfigStore(paths.ConfigDatabase),
    new DpapiCredentialStore(paths.Secrets),
    Environment.MachineName,
    IpcProtocol.DefaultPipeName(Environment.UserName),
    options with { Runner = options.Runner with { OnError = (path, error) => Log($"{path}: {error.Code} {error.Message}") } },
    autostart: new WindowsAutostart(Path.Combine(AppContext.BaseDirectory, "syncdrive.exe")),
    shellIntegration: new ShellMenuRegistrar(
        new CurrentUserClasses(), new SqliteClientConfigStore(paths.ConfigDatabase), Path.Combine(AppContext.BaseDirectory, "syncdrive.exe")),
    cloudFolders: WindowsCloudFolders.IsSupported
        ? new WindowsCloudFolders(
            typeof(DaemonHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", Log,
            CloudRegistrationExecutable(),
            Path.Combine(AppContext.BaseDirectory, "syncdrive.exe") + ",0")
        : null,
    log: Log);

Log("Daemon gestartet");
try
{
    await host.RunAsync(stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    // regulär beendet
}

Log("Daemon beendet");
return 0;

// Im Build liegt das Hilfsprogramm in cloudreg\, in der Installation (ein Ordner, eine Laufzeit) neben dem Daemon.
static string CloudRegistrationExecutable()
{
    var nested = Path.Combine(AppContext.BaseDirectory, "cloudreg", "syncdrive-cloudreg.exe");
    return File.Exists(nested) ? nested : Path.Combine(AppContext.BaseDirectory, "syncdrive-cloudreg.exe");
}
