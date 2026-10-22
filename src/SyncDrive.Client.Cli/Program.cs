using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.Cli;
using SyncDrive.Infra.Ipc;

// syncdrive_cli: steuert den Client-Daemon von der Kommandozeile. Läuft der Daemon nicht, wird er aus dem Programmordner gestartet.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var app = new CliApp(ConnectAsync, Console.Out, Console.Error, ReadPassword);
try
{
    return await app.RunAsync(args, stop.Token);
}
catch (IpcException e)
{
    await Console.Error.WriteLineAsync($"Lost the connection to the SyncDrive client service ({e.Message}).");
    return CliApp.Failed;
}

static async Task<IDaemonClient> ConnectAsync(CancellationToken ct)
{
    var pipe = IpcProtocol.DefaultPipeName(Environment.UserName);
    try
    {
        return await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(1), ct);
    }
    catch (IpcException)
    {
        LaunchDaemon();
    }

    try
    {
        return await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(15), ct);
    }
    catch (IpcException e)
    {
        throw new TimeoutException(e.Message, e);
    }
}

static void LaunchDaemon()
{
    var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "syncdrive-daemon.exe" : "syncdrive-daemon");
    if (!File.Exists(exe))
    {
        throw new InvalidOperationException($"{Path.GetFileName(exe)} not found next to syncdrive_cli");
    }

    try
    {
        using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
    }
    catch (Win32Exception e)
    {
        throw new InvalidOperationException(e.Message, e);
    }
}

// Passwort ohne Echo von der Konsole; bei umgeleiteter Eingabe die erste Zeile.
static string? ReadPassword()
{
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine();
    }

    Console.Error.Write("Password: ");
    var password = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return password.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
            {
                password.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
        }
    }
}
