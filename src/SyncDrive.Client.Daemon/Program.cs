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
if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("Der SyncDrive-Client läuft unter Windows und macOS.");
    return 2;
}

var paths = ClientPaths.Default();
if (args is ["--uninstall"])
{
    return OperatingSystem.IsWindows() ? await UninstallAsync(paths) : 0; // macOS: kein Installer; Aufräumen folgt mit Stufe 3 (ADR-14)
}

paths.EnsureCreated();
var userKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName.ToUpperInvariant())))[..16];
using var single = SingleInstance.TryAcquire($"SyncDrive.Daemon.{userKey}", paths.Root);
if (single is null)
{
    return 0; // läuft schon; die Oberfläche verbindet sich mit dem vorhandenen Daemon
}

// macOS: ein nach einem Absturz liegengebliebener Socket blockierte sonst das Lauschen; nur dieser Prozess darf ihn jetzt anlegen
var endpoint = IpcProtocol.Endpoint(IpcProtocol.DefaultPipeName(Environment.UserName));
if (!OperatingSystem.IsWindows() && File.Exists(endpoint))
{
    File.Delete(endpoint);
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
// Je Plattform die Adapter (ADR-14); Engine, Pipe und Konfiguration sind überall dieselben.
var runner = options with { Runner = options.Runner with { OnError = (path, error) => Log($"{path}: {error.Code} {error.Message}") } };
var config = new SqliteClientConfigStore(paths.ConfigDatabase);
var pipe = IpcProtocol.DefaultPipeName(Environment.UserName);
var host = OperatingSystem.IsWindows()
    ? new DaemonHost(
        paths,
        config,
        new DpapiCredentialStore(paths.Secrets),
        Environment.MachineName,
        pipe,
        runner,
        autostart: new WindowsAutostart(Path.Combine(AppContext.BaseDirectory, "syncdrive.exe")),
        shellIntegration: new ShellMenuRegistrar(new CurrentUserClasses(), config, Path.Combine(AppContext.BaseDirectory, "syncdrive.exe")),
        cloudFolders: WindowsCloudFolders.IsSupported
            ? new WindowsCloudFolders(
                typeof(DaemonHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", Log,
                CloudRegistrationExecutable(),
                Path.Combine(AppContext.BaseDirectory, "syncdrive.exe") + ",0")
            : null,
        log: Log)
    : new DaemonHost(
        paths,
        config,
        OperatingSystem.IsMacOS() ? new KeychainCredentialStore() : throw new PlatformNotSupportedException(),
        Environment.MachineName,
        pipe,
        runner,
        autostart: new MacLaunchAgent(Path.Combine(AppContext.BaseDirectory, "syncdrive")),
        log: Log); // macOS: Finder-Aktionen und Dateien auf Abruf kommen mit der File-Provider-Erweiterung (Stufe 2)

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

// Vom Installer beim Deinstallieren aufgerufen (nicht bei Updates): Oberfläche und Daemon beenden, dann alles Benutzerbezogene entfernen.
static async Task<int> UninstallAsync(ClientPaths paths)
{
    foreach (var process in System.Diagnostics.Process.GetProcessesByName("syncdrive").Concat(System.Diagnostics.Process.GetProcessesByName("syncdrive-daemon")))
    {
        using (process)
        {
            if (process.Id != Environment.ProcessId && process.SessionId == System.Diagnostics.Process.GetCurrentProcess().SessionId)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
    }

    var tray = Path.Combine(AppContext.BaseDirectory, "syncdrive.exe");
    var config = new SqliteClientConfigStore(paths.ConfigDatabase);
    var uninstaller = new ClientUninstaller(
        paths,
        config,
        WindowsCloudFolders.IsSupported ? new WindowsCloudFolders("0.0.0", Console.Error.WriteLine, CloudRegistrationExecutable()) : null,
        new WindowsUninstallSteps(new ShellMenuRegistrar(new CurrentUserClasses(), config, tray), new WindowsAutostart(tray), CloudRegistrationExecutable()),
        Console.Error.WriteLine);
    await uninstaller.RunAsync(CancellationToken.None);
    return 0;
}

// Im Build liegt das Hilfsprogramm in cloudreg\, in der Installation (ein Ordner, eine Laufzeit) neben dem Daemon.
static string CloudRegistrationExecutable()
{
    var nested = Path.Combine(AppContext.BaseDirectory, "cloudreg", "syncdrive-cloudreg.exe");
    return File.Exists(nested) ? nested : Path.Combine(AppContext.BaseDirectory, "syncdrive-cloudreg.exe");
}
