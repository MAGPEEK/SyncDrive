using System.Globalization;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Status;
using SyncDrive.Contracts;
using SyncDrive.Core.Merge;

namespace SyncDrive.Client.Cli;

/// <summary>
/// Befehle von <c>syncdrive_cli</c>. Alles läuft über den Daemon (<see cref="IDaemonClient"/>), genau wie in der Oberfläche: der Daemon allein
/// schreibt Konfiguration und Token. Ausgaben auf Englisch, damit Skripte sie unabhängig von der Systemsprache auswerten können.
/// Rückgabe: 0 erfolgreich, 1 fehlgeschlagen, 2 falscher Aufruf.
/// </summary>
public sealed class CliApp(Func<CancellationToken, Task<IDaemonClient>> connect, TextWriter output, TextWriter error, Func<string?> readPassword)
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;

    private static readonly HashSet<string> Commands = ["connect", "connections", "ls", "sync", "desync", "tasks", "status", "pause", "resume"];

    public const string HelpText = """
        Usage: syncdrive_cli <command> [options]

        Commands:
          connect <server> -u <user> [--password-stdin] [--trust <sha256>]
                                  Sign in to a server (asks for the password unless --password-stdin).
          connections             List server connections.
          ls [<space-id> [<path>]] [-c <connection>]
                                  List spaces, or the folders of a space.
          sync <local-folder> -s <space-id> [-p <server-path>] [-c <connection>]
               [--direction two-way|upload|download] [--on-demand]
                                  Create a sync task.
          desync <task-id|local-folder>
                                  Remove a sync task (local files stay).
          tasks                   List sync tasks.
          status                  Show the sync state of all tasks.
          pause [<task-id>]       Pause one task or all.
          resume [<task-id>]      Resume one task or all.
          version                 Show the client version.

        -c may be omitted when there is exactly one connection; it accepts the id, the name or the server address.
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            await output.WriteLineAsync(HelpText);
            return args.Count == 0 ? Usage : Ok;
        }

        if (args[0] is "version" or "--version")
        {
            await output.WriteLineAsync($"syncdrive_cli {ProductVersion.Current}");
            return Ok;
        }

        if (!Commands.Contains(args[0]))
        {
            return await UsageAsync($"Unknown command '{args[0]}'.");
        }

        var arguments = Arguments.Parse(args.Skip(1));
        if (arguments is null)
        {
            return await UsageAsync("An option is missing its value.");
        }

        IDaemonClient daemon;
        try
        {
            daemon = await connect(ct);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            await error.WriteLineAsync($"The SyncDrive client service is not running and could not be started ({e.Message}).");
            return Failed;
        }

        await using (daemon)
        {
            try
            {
                return args[0] switch
                {
                    "connect" => await ConnectAsync(daemon, arguments, ct),
                    "connections" => await ConnectionsAsync(daemon, ct),
                    "ls" => await ListAsync(daemon, arguments, ct),
                    "sync" => await SyncAsync(daemon, arguments, ct),
                    "desync" => await DesyncAsync(daemon, arguments, ct),
                    "tasks" => await TasksAsync(daemon, ct),
                    "status" => await StatusAsync(daemon, ct),
                    "pause" => await PauseAsync(daemon, arguments, pause: true, ct),
                    _ => await PauseAsync(daemon, arguments, pause: false, ct), // resume
                };
            }
            catch (UnknownTaskException e)
            {
                await error.WriteLineAsync($"Unknown task: {e.TaskId}");
                return Failed;
            }
        }
    }

    private async Task<int> UsageAsync(string message)
    {
        await error.WriteLineAsync(message);
        await error.WriteLineAsync("Run 'syncdrive_cli help' for the list of commands.");
        return Usage;
    }

    private async Task<int> ConnectAsync(IDaemonClient daemon, Arguments arguments, CancellationToken ct)
    {
        if (arguments.Positional is not [var server] || arguments.Value("u", "user") is not { } user)
        {
            return await UsageAsync("connect needs a server address and -u <user>.");
        }

        var password = readPassword(); // mit --password-stdin kommt es aus der umgeleiteten Eingabe, sonst ohne Echo von der Konsole
        if (string.IsNullOrEmpty(password))
        {
            return await UsageAsync("No password given.");
        }

        var outcome = await daemon.SignInAsync(new SignInRequest(server, user, password, arguments.Value("trust")), ct);
        if (outcome.Succeeded)
        {
            await output.WriteLineAsync($"Connected: {outcome.ConnectionId}");
            return Ok;
        }

        if (outcome.ErrorCode == SetupErrors.UntrustedCertificate)
        {
            await error.WriteLineAsync($"The server certificate is not trusted. Fingerprint (SHA-256): {outcome.CertificateSha256}");
            await error.WriteLineAsync("Compare it with the server and repeat the command with --trust <fingerprint>.");
            return Failed;
        }

        await error.WriteLineAsync($"Sign-in failed ({outcome.ErrorCode}): {outcome.Message}");
        return Failed;
    }

    private async Task<int> ConnectionsAsync(IDaemonClient daemon, CancellationToken ct)
    {
        var connections = await daemon.GetConnectionsAsync(ct);
        if (connections.Count == 0)
        {
            await output.WriteLineAsync("No connections. Use 'syncdrive_cli connect' first.");
        }

        foreach (var connection in connections)
        {
            await output.WriteLineAsync($"{connection.Id}\t{connection.UserName}@{connection.ServerAddress}\t{connection.DisplayName}");
        }

        return Ok;
    }

    private async Task<int> ListAsync(IDaemonClient daemon, Arguments arguments, CancellationToken ct)
    {
        if (await ConnectionAsync(daemon, arguments, ct) is not { } connection)
        {
            return Failed;
        }

        var (space, path) = arguments.Positional switch
        {
            [] => (null, string.Empty),
            [var s] => (s, string.Empty),
            [var s, var p, ..] => (s, p.Trim('/')),
        };
        foreach (var folder in await daemon.BrowseAsync(connection.Id, space, path, ct))
        {
            var access = folder.CanWrite ? "rw" : "ro";
            await output.WriteLineAsync(folder.IsSpace ? $"{folder.SpaceId}\t{access}\t{folder.Name}" : $"{folder.Path}/\t{access}");
        }

        return Ok;
    }

    private async Task<int> SyncAsync(IDaemonClient daemon, Arguments arguments, CancellationToken ct)
    {
        if (arguments.Positional is not [var local] || arguments.Value("s", "space") is not { } space)
        {
            return await UsageAsync("sync needs a local folder and -s <space-id>.");
        }

        SyncDirection? direction = arguments.Value("direction") switch
        {
            null or "two-way" => SyncDirection.TwoWay,
            "upload" => SyncDirection.UploadOnly,
            "download" => SyncDirection.DownloadOnly,
            _ => null,
        };
        if (direction is null)
        {
            return await UsageAsync("--direction must be two-way, upload or download.");
        }

        if (await ConnectionAsync(daemon, arguments, ct) is not { } connection)
        {
            return Failed;
        }

        var outcome = await daemon.CreateTaskAsync(
            new NewTaskRequest(
                connection.Id, space, (arguments.Value("p", "path") ?? string.Empty).Trim('/'), Path.GetFullPath(local), direction.Value,
                OnDemand: arguments.Has("on-demand")),
            ct);
        if (outcome.Succeeded)
        {
            await output.WriteLineAsync($"Sync task created: {outcome.TaskId}");
            return Ok;
        }

        var reasons = outcome.Violations.Count > 0 ? string.Join(", ", outcome.Violations) : $"{outcome.ErrorCode}: {outcome.Message}";
        await error.WriteLineAsync($"The sync task was not created ({reasons}).");
        return Failed;
    }

    private async Task<int> DesyncAsync(IDaemonClient daemon, Arguments arguments, CancellationToken ct)
    {
        if (arguments.Positional is not [var target])
        {
            return await UsageAsync("desync needs a task id or a local folder.");
        }

        var full = Path.GetFullPath(target).TrimEnd('\\', '/');
        var task = (await daemon.GetTasksAsync(ct)).FirstOrDefault(t =>
            t.TaskId == target || string.Equals(t.LocalPath.TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase));
        if (task is null)
        {
            await error.WriteLineAsync($"No sync task for '{target}'.");
            return Failed;
        }

        await daemon.DeleteTaskAsync(task.TaskId, ct);
        await output.WriteLineAsync($"Sync task removed: {task.TaskId} ({task.LocalPath})");
        return Ok;
    }

    private async Task<int> TasksAsync(IDaemonClient daemon, CancellationToken ct)
    {
        foreach (var task in await daemon.GetTasksAsync(ct))
        {
            var mode = task.Direction switch
            {
                SyncDirection.UploadOnly => "upload",
                SyncDirection.DownloadOnly => "download",
                _ => "two-way",
            } + (task.OnDemand ? ",on-demand" : string.Empty) + (task.Paused ? ",paused" : string.Empty);
            await output.WriteLineAsync($"{task.TaskId}\t{task.LocalPath}\t{task.ServerAddress}/{task.SpaceId}/{task.RemotePath}\t{mode}");
        }

        return Ok;
    }

    private async Task<int> StatusAsync(IDaemonClient daemon, CancellationToken ct)
    {
        var status = await daemon.GetStatusAsync(ct);
        var paths = (await daemon.GetTasksAsync(ct)).ToDictionary(t => t.TaskId, t => t.LocalPath);
        await output.WriteLineAsync($"Overall: {status.Tray}" + (status.Pending > 0 ? $", {status.Pending} pending" : string.Empty));
        foreach (var task in status.Tasks)
        {
            var line = $"{task.TaskId}\t{paths.GetValueOrDefault(task.TaskId, "?")}\t{task.State}";
            if (task.State == TaskState.Syncing && task.Pending > 0)
            {
                line += $" ({task.Pending} pending)";
            }

            if (task.LastSyncedAt is { } synced)
            {
                line += $"\tlast sync {synced.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
            }

            if (task.LastError is { } problem)
            {
                line += $"\terror: {problem}";
            }

            await output.WriteLineAsync(line);
        }

        return status.Tasks.Any(t => t.State == TaskState.Error) ? Failed : Ok;
    }

    private async Task<int> PauseAsync(IDaemonClient daemon, Arguments arguments, bool pause, CancellationToken ct)
    {
        var task = arguments.Positional is [var id] ? id : null;
        await (pause ? daemon.PauseAsync(task, ct) : daemon.ResumeAsync(task, ct));
        await output.WriteLineAsync((pause ? "Paused: " : "Resumed: ") + (task ?? "all tasks"));
        return Ok;
    }

    /// <summary>Verbindung aus <c>-c</c> (Id, Name oder Adresse) oder die einzige vorhandene.</summary>
    private async Task<ConnectionSummary?> ConnectionAsync(IDaemonClient daemon, Arguments arguments, CancellationToken ct)
    {
        var connections = await daemon.GetConnectionsAsync(ct);
        var wanted = arguments.Value("c", "connection");
        var matches = wanted is null
            ? connections
            : [.. connections.Where(c => c.Id == wanted
                || string.Equals(c.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.ServerAddress, wanted, StringComparison.OrdinalIgnoreCase))];
        if (matches.Count == 1)
        {
            return matches[0];
        }

        await error.WriteLineAsync(matches.Count == 0
            ? wanted is null ? "No connections. Use 'syncdrive_cli connect' first." : $"No connection '{wanted}'."
            : "Several connections exist; choose one with -c (see 'syncdrive_cli connections').");
        return null;
    }

    /// <summary>Positionsargumente und Optionen (<c>-x wert</c>, <c>--name wert</c>, Schalter ohne Wert).</summary>
    private sealed class Arguments
    {
        private static readonly HashSet<string> Switches = ["password-stdin", "on-demand"];
        private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

        public List<string> Positional { get; } = [];

        public static Arguments? Parse(IEnumerable<string> args)
        {
            var parsed = new Arguments();
            using var items = args.GetEnumerator();
            while (items.MoveNext())
            {
                var item = items.Current;
                if (item.Length < 2 || item[0] != '-')
                {
                    parsed.Positional.Add(item);
                    continue;
                }

                var name = item.TrimStart('-');
                if (Switches.Contains(name))
                {
                    parsed._options[name] = null;
                }
                else if (items.MoveNext())
                {
                    parsed._options[name] = items.Current;
                }
                else
                {
                    return null;
                }
            }

            return parsed;
        }

        public bool Has(string name) => _options.ContainsKey(name);

        public string? Value(params string[] names) => names.Select(n => _options.GetValueOrDefault(n)).FirstOrDefault(v => v is not null);
    }
}
