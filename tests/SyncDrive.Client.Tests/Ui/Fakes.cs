using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tests.Ui;

/// <summary>Daemon zum Vorspielen: Zustände kommen über <see cref="Publish"/>, Aufrufe werden mitgeschrieben.</summary>
internal sealed class FakeDaemon : IDaemonClient
{
    private readonly Channel<OverallStatus> _status = Channel.CreateUnbounded<OverallStatus>();

    public event Action? Disconnected;

    public bool IsConnected { get; private set; } = true;

    public List<string> Calls { get; } = [];

    public List<TaskSummary> Tasks { get; } = [];

    public List<ConnectionSummary> Connections { get; } = [];

    public List<TaskActivity> Activity { get; } = [];

    public ClientSettings Settings { get; set; } = new();

    public List<ClientSettings> SavedSettings { get; } = [];

    public Queue<SignInOutcome> SignInResults { get; } = new();

    public List<SignInRequest> SignIns { get; } = [];

    public List<NewTaskRequest> Created { get; } = [];

    public Func<string, IReadOnlyList<TaskRuleViolation>> CheckFolder { get; set; } = _ => [];

    public Dictionary<string, List<ServerFolder>> Folders { get; } = [];

    public void Publish(OverallStatus status) => _status.Writer.TryWrite(status);

    /// <summary>Wie ein abgestürzter Daemon: der Beobachter endet mit Fehler.</summary>
    public void Drop()
    {
        IsConnected = false;
        _status.Writer.TryComplete(new IOException("weg"));
        Disconnected?.Invoke();
    }

    public OverallStatus Status { get; set; } = OverallStatus.Combine([], false);

    public Task<OverallStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);

    public Task<IReadOnlyList<TaskActivity>> GetRecentActivityAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TaskActivity>>([.. Activity.OrderByDescending(a => a.Entry.At).Take(limit)]);

    public Task PauseAsync(string? taskId, CancellationToken ct) => Record($"pause {taskId ?? "*"}");

    public Task ResumeAsync(string? taskId, CancellationToken ct) => Record($"resume {taskId ?? "*"}");

    public async IAsyncEnumerable<OverallStatus> WatchStatusAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var status in _status.Reader.ReadAllAsync(ct))
        {
            yield return status;
        }
    }

    public Task<IReadOnlyList<TaskSummary>> GetTasksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskSummary>>([.. Tasks]);

    public Task<IReadOnlyList<ConnectionSummary>> GetConnectionsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConnectionSummary>>([.. Connections]);

    public Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct)
    {
        SignIns.Add(request);
        return Task.FromResult(SignInResults.Dequeue());
    }

    public Task<IReadOnlyList<ServerFolder>> BrowseAsync(string connectionId, string? spaceId, string path, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ServerFolder>>(Folders.GetValueOrDefault($"{spaceId}:{path}") ?? []);

    public Task<IReadOnlyList<TaskRuleViolation>> CheckLocalFolderAsync(string localPath, bool onDemand, CancellationToken ct) =>
        Task.FromResult(CheckFolder(localPath));

    public Task<TaskOutcome> CreateTaskAsync(NewTaskRequest request, CancellationToken ct)
    {
        Created.Add(request);
        return Task.FromResult(new TaskOutcome("t-neu", []));
    }

    public Task DeleteTaskAsync(string taskId, CancellationToken ct)
    {
        Tasks.RemoveAll(t => t.TaskId == taskId);
        return Record($"delete {taskId}");
    }

    public Task<ClientSettings> GetSettingsAsync(CancellationToken ct) => Task.FromResult(Settings);

    public Task SaveSettingsAsync(ClientSettings settings, CancellationToken ct)
    {
        SavedSettings.Add(settings);
        return Task.CompletedTask;
    }

    public List<ServerNotification> Notifications { get; } = [];

    public Dictionary<string, SyncTaskDefinition> Definitions { get; } = [];

    public List<SyncTaskDefinition> Updated { get; } = [];

    public Queue<SignInOutcome> ReSignInResults { get; } = new();

    public List<string?> ProxyPasswords { get; } = [];

    public Task<SyncTaskDefinition?> GetTaskAsync(string taskId, CancellationToken ct) => Task.FromResult(Definitions.GetValueOrDefault(taskId));

    public Task<TaskOutcome> UpdateTaskAsync(SyncTaskDefinition task, CancellationToken ct)
    {
        Updated.Add(task);
        return Task.FromResult(new TaskOutcome(task.Id, []));
    }

    public Task<SignInOutcome> UpdateConnectionAsync(string connectionId, SignInRequest request, CancellationToken ct)
    {
        SignIns.Add(request);
        return Task.FromResult(ReSignInResults.Dequeue());
    }

    public Task<IReadOnlyList<TaskUsage>> GetUsageAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TaskUsage>>([.. Tasks.Select((t, i) => new TaskUsage(t.TaskId, (i + 1) * 1536L * 1024 * 1024, 10))]);

    public Task SetProxyPasswordAsync(string? password, CancellationToken ct)
    {
        ProxyPasswords.Add(password);
        return Task.CompletedTask;
    }

    public int NotificationQueries { get; private set; }

    public Task<IReadOnlyList<ServerNotification>> GetNotificationsAsync(CancellationToken ct)
    {
        NotificationQueries++;
        return Task.FromResult<IReadOnlyList<ServerNotification>>([.. Notifications]);
    }

    public Task MarkNotificationsReadAsync(CancellationToken ct)
    {
        for (var i = 0; i < Notifications.Count; i++)
        {
            Notifications[i] = Notifications[i] with { Read = true };
        }

        return Record("read notifications");
    }

    public PathInfo? PathInfo { get; set; } = new("t1", "a.txt", false, PathStatus.UpToDate, false);

    public List<FileVersion> Versions { get; } = [];

    public Queue<ShellResult> ShellResults { get; } = new();

    public Task<PathInfo?> GetPathInfoAsync(string localPath, CancellationToken ct) => Task.FromResult(PathInfo);

    public Task<IReadOnlyList<FileVersion>> ListVersionsAsync(string localPath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FileVersion>>([.. Versions]);

    public async Task<ShellResult> SaveVersionAsAsync(string localPath, long revision, string targetPath, CancellationToken ct)
    {
        await Record($"save {revision} {targetPath}");
        return Next();
    }

    public async Task<ShellResult> RestoreVersionAsync(string localPath, long revision, CancellationToken ct)
    {
        await Record($"restore {revision}");
        return Next();
    }

    public async Task<ShellResult> SetFolderSyncedAsync(string localPath, bool synced, CancellationToken ct)
    {
        await Record($"synced={synced} {localPath}");
        return Next();
    }

    public async Task<ShellResult> SetPinnedAsync(string localPath, bool keep, CancellationToken ct)
    {
        await Record($"keep={keep} {localPath}");
        return Next();
    }

    public async Task<ShellResult> GetLinkAsync(string localPath, CancellationToken ct)
    {
        await Record($"link {localPath}");
        return Next();
    }

    private ShellResult Next() => ShellResults.Count > 0 ? ShellResults.Dequeue() : ShellResult.Ok;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task Record(string call)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Oberflächendienste ohne Fenster: Dialoge antworten wie vorgegeben, Aufrufe werden mitgeschrieben.</summary>
internal sealed class FakeUi : IUiServices
{
    public List<string> Messages { get; } = [];

    public List<string> Copied { get; } = [];

    public List<string> Opened { get; } = [];

    public List<string> Actions { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public string? LocalFolderAnswer { get; set; }

    public Func<ServerFolderPickerViewModel, Task<(string, string, string)?>>? ServerFolderAnswer { get; set; }

    public void Post(Action action) => action();

    public Task<string?> PickLocalFolderAsync(string? suggestion) => Task.FromResult(LocalFolderAnswer);

    public Task<(string SpaceId, string Path, string DisplayPath)?> PickServerFolderAsync(ServerFolderPickerViewModel picker) =>
        ServerFolderAnswer?.Invoke(picker) ?? Task.FromResult<(string, string, string)?>(null);

    public Task ShowMessageAsync(string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task CopyAndShowAsync(string text, string message)
    {
        Copied.Add(text);
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string message)
    {
        Messages.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public void OpenFolder(string path) => Opened.Add(path);

    public void OpenMainWindow(MainPage page) => Actions.Add($"main {page}");

    public void OpenWizard() => Actions.Add("wizard");

    public void ShowAbout() => Actions.Add("about");

    public List<string> Toasts { get; } = [];

    public Func<EditConnectionViewModel, Task>? EditConnection { get; set; }

    public Func<SyncRulesViewModel, Task>? SyncRules { get; set; }

    public void ShowToast(string title, string message) => Toasts.Add(message);

    public Task ShowEditConnectionAsync(EditConnectionViewModel dialog) => EditConnection?.Invoke(dialog) ?? Task.CompletedTask;

    public Task ShowSyncRulesAsync(SyncRulesViewModel dialog) => SyncRules?.Invoke(dialog) ?? Task.CompletedTask;

    public Func<VersionsViewModel, Task>? Versions { get; set; }

    public string? SaveFileAnswer { get; set; }

    public Task ShowVersionsAsync(VersionsViewModel dialog) => Versions?.Invoke(dialog) ?? Task.CompletedTask;

    public Task<string?> PickSaveFileAsync(string suggestedName, string? folder)
    {
        Actions.Add($"save-dialog {suggestedName}");
        return Task.FromResult(SaveFileAnswer);
    }

    public void Quit() => Actions.Add("quit");
}
