using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Tasks;

namespace SyncDrive.Infra.Ipc;

/// <summary>
/// Oberflächen-Seite: <see cref="IDaemonControl"/> über die Pipe. Bricht die Verbindung ab, scheitern laufende und künftige Aufrufe mit
/// <see cref="IpcException"/> (<c>disconnected</c>), Beobachter enden; die Oberfläche verbindet sich dann neu.
/// </summary>
public sealed class IpcClient : IDaemonClient
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly List<Channel<OverallStatus>> _watchers = [];
    private readonly Lock _watchersGate = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reading;
    private long _nextId;
    private volatile bool _disconnected;

    private IpcClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        _reading = ReadLoopAsync();
    }

    /// <summary>Die Verbindung ist weg (Daemon beendet oder abgestürzt).</summary>
    public event Action? Disconnected;

    public bool IsConnected => !_disconnected;

    public static async Task<IpcClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", IpcProtocol.Endpoint(pipeName), PipeDirection.InOut, IpcProtocol.Options);
        try
        {
            await pipe.ConnectAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync();
            throw new IpcException(IpcProtocol.Errors.Disconnected, "Daemon nicht erreichbar.");
        }

        return new IpcClient(pipe);
    }

    public async Task<OverallStatus> GetStatusAsync(CancellationToken ct) =>
        Result<OverallStatus>(await CallAsync(IpcProtocol.Methods.Status, null, ct));

    public async Task<IReadOnlyList<TaskActivity>> GetRecentActivityAsync(int limit, CancellationToken ct) =>
        Result<List<TaskActivity>>(await CallAsync(IpcProtocol.Methods.Activity, new LimitParams(limit), ct));

    public Task PauseAsync(string? taskId, CancellationToken ct) => CallAsync(IpcProtocol.Methods.Pause, new TaskParams(taskId), ct);

    public Task ResumeAsync(string? taskId, CancellationToken ct) => CallAsync(IpcProtocol.Methods.Resume, new TaskParams(taskId), ct);

    public async Task<IReadOnlyList<TaskSummary>> GetTasksAsync(CancellationToken ct) =>
        Result<List<TaskSummary>>(await CallAsync(IpcProtocol.Methods.Tasks, null, ct));

    public async Task<IReadOnlyList<ConnectionSummary>> GetConnectionsAsync(CancellationToken ct) =>
        Result<List<ConnectionSummary>>(await CallAsync(IpcProtocol.Methods.Connections, null, ct));

    public async Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct) =>
        Result<SignInOutcome>(await CallAsync(IpcProtocol.Methods.SignIn, request, ct));

    public async Task<IReadOnlyList<ServerFolder>> BrowseAsync(string connectionId, string? spaceId, string path, CancellationToken ct) =>
        Result<List<ServerFolder>>(await CallAsync(IpcProtocol.Methods.Browse, new BrowseParams(connectionId, spaceId, path), ct));

    public async Task<IReadOnlyList<TaskRuleViolation>> CheckLocalFolderAsync(string localPath, bool onDemand, CancellationToken ct) =>
        Result<List<TaskRuleViolation>>(await CallAsync(IpcProtocol.Methods.CheckFolder, new CheckFolderParams(localPath, onDemand), ct));

    public async Task<TaskOutcome> CreateTaskAsync(NewTaskRequest request, CancellationToken ct) =>
        Result<TaskOutcome>(await CallAsync(IpcProtocol.Methods.CreateTask, request, ct));

    public Task DeleteTaskAsync(string taskId, CancellationToken ct) => CallAsync(IpcProtocol.Methods.DeleteTask, new TaskParams(taskId), ct);

    public async Task<ClientSettings> GetSettingsAsync(CancellationToken ct) =>
        Result<ClientSettings>(await CallAsync(IpcProtocol.Methods.GetSettings, null, ct));

    public Task SaveSettingsAsync(ClientSettings settings, CancellationToken ct) => CallAsync(IpcProtocol.Methods.SaveSettings, settings, ct);

    public async Task<SyncTaskDefinition?> GetTaskAsync(string taskId, CancellationToken ct)
    {
        var reply = await CallAsync(IpcProtocol.Methods.GetTask, new TaskParams(taskId), ct);
        return reply.Result is { ValueKind: not System.Text.Json.JsonValueKind.Null } ? Result<SyncTaskDefinition>(reply) : null;
    }

    public async Task<TaskOutcome> UpdateTaskAsync(SyncTaskDefinition task, CancellationToken ct) =>
        Result<TaskOutcome>(await CallAsync(IpcProtocol.Methods.UpdateTask, task, ct));

    public async Task<SignInOutcome> UpdateConnectionAsync(string connectionId, SignInRequest request, CancellationToken ct) =>
        Result<SignInOutcome>(await CallAsync(IpcProtocol.Methods.UpdateConnection, new UpdateConnectionParams(connectionId, request), ct));

    public async Task<IReadOnlyList<TaskUsage>> GetUsageAsync(CancellationToken ct) =>
        Result<List<TaskUsage>>(await CallAsync(IpcProtocol.Methods.Usage, null, ct));

    public Task SetProxyPasswordAsync(string? password, CancellationToken ct) => CallAsync(IpcProtocol.Methods.ProxyPassword, new PasswordParams(password), ct);

    public async Task<IReadOnlyList<ServerNotification>> GetNotificationsAsync(CancellationToken ct) =>
        Result<List<ServerNotification>>(await CallAsync(IpcProtocol.Methods.Notifications, null, ct));

    public Task MarkNotificationsReadAsync(CancellationToken ct) => CallAsync(IpcProtocol.Methods.NotificationsRead, null, ct);

    public async Task<PathInfo?> GetPathInfoAsync(string localPath, CancellationToken ct)
    {
        var reply = await CallAsync(IpcProtocol.Methods.PathInfo, new PathParams(localPath), ct);
        return reply.Result is { ValueKind: not System.Text.Json.JsonValueKind.Null } ? Result<PathInfo>(reply) : null;
    }

    public async Task<IReadOnlyList<FileVersion>> ListVersionsAsync(string localPath, CancellationToken ct) =>
        Result<List<FileVersion>>(await CallAsync(IpcProtocol.Methods.Versions, new PathParams(localPath), ct));

    public async Task<ShellResult> SaveVersionAsAsync(string localPath, long revision, string targetPath, CancellationToken ct) =>
        Result<ShellResult>(await CallAsync(IpcProtocol.Methods.SaveVersion, new VersionParams(localPath, revision, targetPath), ct));

    public async Task<ShellResult> RestoreVersionAsync(string localPath, long revision, CancellationToken ct) =>
        Result<ShellResult>(await CallAsync(IpcProtocol.Methods.RestoreVersion, new VersionParams(localPath, revision), ct));

    public async Task<ShellResult> SetFolderSyncedAsync(string localPath, bool synced, CancellationToken ct) =>
        Result<ShellResult>(await CallAsync(IpcProtocol.Methods.FolderSync, new FolderSyncParams(localPath, synced), ct));

    public async Task<ShellResult> SetPinnedAsync(string localPath, bool keep, CancellationToken ct) =>
        Result<ShellResult>(await CallAsync(IpcProtocol.Methods.Pin, new PinParams(localPath, keep), ct));

    public async Task<ShellResult> GetLinkAsync(string localPath, CancellationToken ct) =>
        Result<ShellResult>(await CallAsync(IpcProtocol.Methods.Link, new PathParams(localPath), ct));

    public async IAsyncEnumerable<OverallStatus> WatchStatusAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<OverallStatus>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_watchersGate)
        {
            _watchers.Add(channel);
        }

        try
        {
            await CallAsync(IpcProtocol.Methods.Watch, null, ct);
            await foreach (var status in channel.Reader.ReadAllAsync(ct))
            {
                yield return status;
            }
        }
        finally
        {
            lock (_watchersGate)
            {
                _watchers.Remove(channel);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        await _pipe.DisposeAsync();
        await _reading.ContinueWith(_ => { }, TaskScheduler.Default);
        await _writer.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
        _writeGate.Dispose();
        _closing.Dispose();
    }

    private async Task<IpcMessage> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);
        if (_disconnected)
        {
            throw new IpcException(IpcProtocol.Errors.Disconnected, "Keine Verbindung zum Daemon.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var request = new IpcRequest(
                IpcProtocol.Version, id, method, parameters is null ? null : JsonSerializer.SerializeToElement(parameters, IpcProtocol.Json));
            var line = JsonSerializer.Serialize(request, IpcProtocol.Json);
            await _writeGate.WaitAsync(ct);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), ct);
                await _writer.FlushAsync(ct);
            }
            catch (IOException)
            {
                throw new IpcException(IpcProtocol.Errors.Disconnected, "Keine Verbindung zum Daemon.");
            }
            finally
            {
                _writeGate.Release();
            }

            var message = await reply.Task.WaitAsync(ct);
            return message.Ok == true ? message : throw new IpcException(message.Error?.Code ?? IpcProtocol.Errors.Internal, message.Error?.Message ?? string.Empty);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static T Result<T>(IpcMessage message) =>
        message.Result is { } result
            ? result.Deserialize<T>(IpcProtocol.Json) ?? throw new IpcException(IpcProtocol.Errors.BadRequest, "Leere Antwort.")
            : throw new IpcException(IpcProtocol.Errors.BadRequest, "Antwort ohne Ergebnis.");

    private async Task ReadLoopAsync()
    {
        using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(_closing.Token) is { } line)
            {
                Dispatch(JsonSerializer.Deserialize<IpcMessage>(line, IpcProtocol.Json));
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
            // Verbindung beendet oder Unsinn empfangen: wie ein Abbruch behandeln
        }
        finally
        {
            OnDisconnected();
        }
    }

    private void Dispatch(IpcMessage? message)
    {
        if (message is { Event: IpcProtocol.StatusEvent, Data: { } data })
        {
            var status = data.Deserialize<OverallStatus>(IpcProtocol.Json);
            if (status is not null)
            {
                foreach (var watcher in Watchers())
                {
                    watcher.Writer.TryWrite(status);
                }
            }
        }
        else if (message is { Id: { } id } && _pending.TryGetValue(id, out var reply))
        {
            reply.TrySetResult(message);
        }
    }

    private void OnDisconnected()
    {
        _disconnected = true;
        var error = new IpcException(IpcProtocol.Errors.Disconnected, "Verbindung zum Daemon getrennt.");
        foreach (var reply in _pending.Values)
        {
            reply.TrySetException(error);
        }

        foreach (var watcher in Watchers())
        {
            watcher.Writer.TryComplete(error);
        }

        Disconnected?.Invoke();
    }

    private Channel<OverallStatus>[] Watchers()
    {
        lock (_watchersGate)
        {
            return [.. _watchers];
        }
    }
}
