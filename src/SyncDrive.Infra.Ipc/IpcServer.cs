using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;

namespace SyncDrive.Infra.Ipc;

/// <summary>Daemon-Seite: nimmt beliebig viele Verbindungen der Oberfläche an und leitet sie an <see cref="IDaemonControl"/> weiter.</summary>
public sealed class IpcServer(string pipeName, IDaemonControl control, IClientSetup? setup = null, IShellActions? shell = null)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var connections = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, IpcProtocol.Options);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            connections.RemoveAll(c => c.IsCompleted);
            connections.Add(ServeAsync(pipe, ct));
        }

        await Task.WhenAll(connections.Select(c => c.ContinueWith(_ => { }, TaskScheduler.Default)));
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var writeGate = new SemaphoreSlim(1, 1);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using (writer)
        {
            async Task SendAsync(IpcMessage message)
            {
                var line = JsonSerializer.Serialize(message, IpcProtocol.Json);
                await writeGate.WaitAsync(connection.Token);
                try
                {
                    await writer.WriteLineAsync(line.AsMemory(), connection.Token);
                    await writer.FlushAsync(connection.Token);
                }
                finally
                {
                    writeGate.Release();
                }
            }

            Task? watching = null;
            try
            {
                while (await reader.ReadLineAsync(connection.Token) is { } line)
                {
                    var request = Parse(line);
                    if (request is null)
                    {
                        await SendAsync(Failure(0, IpcProtocol.Errors.BadRequest, "Anfrage ist kein gültiges JSON."));
                        continue;
                    }

                    if (request.V != IpcProtocol.Version)
                    {
                        await SendAsync(Failure(request.Id, IpcProtocol.Errors.UnsupportedVersion, $"Daemon spricht Version {IpcProtocol.Version}."));
                        continue;
                    }

                    if (request.Method == IpcProtocol.Methods.Watch)
                    {
                        watching ??= WatchAsync(SendAsync, connection.Token);
                        await SendAsync(new IpcMessage(request.Id, Ok: true));
                        continue;
                    }

                    await SendAsync(await HandleAsync(request, connection.Token));
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Oberfläche hat die Verbindung getrennt oder der Daemon endet
            }
            finally
            {
                await connection.CancelAsync();
                if (watching is not null)
                {
                    await watching.ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
        }
    }

    private async Task WatchAsync(Func<IpcMessage, Task> send, CancellationToken ct)
    {
        await foreach (var status in control.WatchStatusAsync(ct))
        {
            await send(new IpcMessage(Event: IpcProtocol.StatusEvent, Data: JsonSerializer.SerializeToElement(status, IpcProtocol.Json)));
        }
    }

    private async Task<IpcMessage> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            object? result = request.Method switch
            {
                IpcProtocol.Methods.Status => await control.GetStatusAsync(ct),
                IpcProtocol.Methods.Activity => await control.GetRecentActivityAsync(Math.Clamp(Params<LimitParams>(request)?.Limit ?? 50, 1, 1000), ct),
                IpcProtocol.Methods.Pause => await Done(control.PauseAsync(Params<TaskParams>(request)?.TaskId, ct)),
                IpcProtocol.Methods.Resume => await Done(control.ResumeAsync(Params<TaskParams>(request)?.TaskId, ct)),
                IpcProtocol.Methods.Tasks => await Setup().GetTasksAsync(ct),
                IpcProtocol.Methods.Connections => await Setup().GetConnectionsAsync(ct),
                IpcProtocol.Methods.SignIn => await Setup().SignInAsync(Required<SignInRequest>(request), ct),
                IpcProtocol.Methods.Browse => await Browse(Required<BrowseParams>(request), ct),
                IpcProtocol.Methods.CheckFolder => await CheckFolder(Required<CheckFolderParams>(request), ct),
                IpcProtocol.Methods.CreateTask => await Setup().CreateTaskAsync(Required<NewTaskRequest>(request), ct),
                IpcProtocol.Methods.DeleteTask => await Done(Setup().DeleteTaskAsync(Required<TaskParams>(request).TaskId ?? string.Empty, ct)),
                IpcProtocol.Methods.GetSettings => await Setup().GetSettingsAsync(ct),
                IpcProtocol.Methods.SaveSettings => await Done(Setup().SaveSettingsAsync(Required<ClientSettings>(request), ct)),
                IpcProtocol.Methods.GetTask => await Setup().GetTaskAsync(Required<TaskParams>(request).TaskId ?? string.Empty, ct),
                IpcProtocol.Methods.UpdateTask => await Setup().UpdateTaskAsync(Required<SyncTaskDefinition>(request), ct),
                IpcProtocol.Methods.UpdateConnection => await UpdateConnection(Required<UpdateConnectionParams>(request), ct),
                IpcProtocol.Methods.Usage => await Setup().GetUsageAsync(ct),
                IpcProtocol.Methods.ProxyPassword => await Done(Setup().SetProxyPasswordAsync(Params<PasswordParams>(request)?.Password, ct)),
                IpcProtocol.Methods.Notifications => await Setup().GetNotificationsAsync(ct),
                IpcProtocol.Methods.NotificationsRead => await Done(Setup().MarkNotificationsReadAsync(ct)),
                IpcProtocol.Methods.PathInfo => await Shell().GetPathInfoAsync(Required<PathParams>(request).Path, ct),
                IpcProtocol.Methods.Versions => await Shell().ListVersionsAsync(Required<PathParams>(request).Path, ct),
                IpcProtocol.Methods.SaveVersion => await SaveVersion(Required<VersionParams>(request), ct),
                IpcProtocol.Methods.RestoreVersion => await RestoreVersion(Required<VersionParams>(request), ct),
                IpcProtocol.Methods.FolderSync => await FolderSync(Required<FolderSyncParams>(request), ct),
                _ => throw new IpcException(IpcProtocol.Errors.UnknownMethod, request.Method),
            };
            return new IpcMessage(request.Id, Ok: true, Result: result is null ? null : JsonSerializer.SerializeToElement(result, IpcProtocol.Json));
        }
        catch (IpcException e)
        {
            return Failure(request.Id, e.Code, e.Message);
        }
        catch (UnknownTaskException e)
        {
            return Failure(request.Id, IpcProtocol.Errors.UnknownTask, e.Message);
        }
        catch (JsonException e)
        {
            return Failure(request.Id, IpcProtocol.Errors.BadRequest, e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Failure(request.Id, IpcProtocol.Errors.Internal, e.Message);
        }
    }

    private IClientSetup Setup() => setup ?? throw new IpcException(IpcProtocol.Errors.UnknownMethod, "Einrichtung wird hier nicht angeboten.");

    private Task<IReadOnlyList<ServerFolder>> Browse(BrowseParams p, CancellationToken ct) => Setup().BrowseAsync(p.ConnectionId, p.SpaceId, p.Path, ct);

    private Task<IReadOnlyList<TaskRuleViolation>> CheckFolder(CheckFolderParams p, CancellationToken ct) =>
        Setup().CheckLocalFolderAsync(p.LocalPath, p.OnDemand, ct);

    private Task<SignInOutcome> UpdateConnection(UpdateConnectionParams p, CancellationToken ct) => Setup().UpdateConnectionAsync(p.ConnectionId, p.Request, ct);

    private IShellActions Shell() => shell ?? throw new IpcException(IpcProtocol.Errors.UnknownMethod, "Explorer-Aktionen werden hier nicht angeboten.");

    private Task<ShellResult> SaveVersion(VersionParams p, CancellationToken ct) =>
        Shell().SaveVersionAsAsync(p.Path, p.Revision, p.Target ?? throw new IpcException(IpcProtocol.Errors.BadRequest, "Ziel fehlt."), ct);

    private Task<ShellResult> RestoreVersion(VersionParams p, CancellationToken ct) => Shell().RestoreVersionAsync(p.Path, p.Revision, ct);

    private Task<ShellResult> FolderSync(FolderSyncParams p, CancellationToken ct) => Shell().SetFolderSyncedAsync(p.Path, p.Synced, ct);

    private static T Required<T>(IpcRequest request) =>
        Params<T>(request) ?? throw new IpcException(IpcProtocol.Errors.BadRequest, "Parameter fehlen.");

    private static async Task<object?> Done(Task task)
    {
        await task;
        return null;
    }

    private static T? Params<T>(IpcRequest request) =>
        request.Params is { ValueKind: JsonValueKind.Object } p ? p.Deserialize<T>(IpcProtocol.Json) : default;

    private static IpcRequest? Parse(string line)
    {
        try
        {
            var request = JsonSerializer.Deserialize<IpcRequest>(line, IpcProtocol.Json);
            return request is { Method: not null } ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IpcMessage Failure(long id, string code, string message) => new(id, Ok: false, Error: new IpcError(code, message));
}
