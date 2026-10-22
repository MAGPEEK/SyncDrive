using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Client.Ui;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;
using TaskStatus = SyncDrive.Client.App.Status.TaskStatus;

[assembly: AvaloniaTestApplication(typeof(SyncDrive.Client.Tray.Tests.TestApp))]

namespace SyncDrive.Client.Tray.Tests;

/// <summary>Die echte App (Stile, Ressourcen) ohne Bildschirm; gerendert wird mit Skia, damit Bilder entstehen.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" }) // gleiche Schrift unter Windows und im Linux-Container
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Daemon mit ausgedachten Beispieldaten (keine echten Namen, Pfade oder Server).</summary>
internal sealed class DemoDaemon : IDaemonClient
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public event Action? Disconnected;

    public bool IsConnected => true;

    public List<TaskSummary> Tasks { get; } =
    [
        new("t1", "c1", "Heim-NAS", "nas.example:8443", "s1", string.Empty, @"C:\Users\Beispiel\SyncDrive", SyncDirection.TwoWay, false, false),
        new("t2", "c1", "Heim-NAS", "nas.example:8443", "s2", "Fotos", @"D:\Fotos", SyncDirection.DownloadOnly, true, false),
        new("t3", "c2", "Büro", "buero.example:8443", "s3", "Projekte", @"C:\Users\Beispiel\Projekte", SyncDirection.UploadOnly, false, false),
    ];

    public List<ConnectionSummary> Connections { get; } = [new("c1", "Heim-NAS", "nas.example:8443", "anna"), new("c2", "Büro", "buero.example:8443", "anna")];

    public List<TaskActivity> Activity { get; } =
    [
        new("t1", new(Now.AddMinutes(-3), ActivityAction.Upload, RelativePath.Parse("Angebote/Angebot-2026-10.docx"), false, ActivityResult.Done)),
        new("t1", new(Now.AddMinutes(-7), ActivityAction.Upload, RelativePath.Parse("Angebote/~$Angebot-2026-10.docx"), false, ActivityResult.Unsynced, "filter")),
        new("t2", new(Now.AddHours(-5), ActivityAction.Download, RelativePath.Parse("Urlaub/Strand-0042.jpg"), false, ActivityResult.Done)),
        new("t1", new(Now.AddHours(-9), ActivityAction.Delete, RelativePath.Parse("Alt/Entwurf.txt"), false, ActivityResult.Done)),
        new("t3", new(Now.AddDays(-1), ActivityAction.Download, RelativePath.Parse("Plan/Zeitplan.xlsx"), false, ActivityResult.Done)),
        new("t3", new(Now.AddDays(-3), ActivityAction.Conflict, RelativePath.Parse("Plan/Budget (Konflikt Laptop).pdf"), false, ActivityResult.Done)),
    ];

    public static OverallStatus Status(TrayState tray) => OverallStatus.Combine(
        tray switch
        {
            TrayState.Syncing => [Task("t1", TaskState.Syncing, "Angebote/Angebot-2026-10.docx"), Task("t2", TaskState.Done), Task("t3", TaskState.Done)],
            TrayState.Paused => [Task("t1", TaskState.Paused), Task("t2", TaskState.Paused), Task("t3", TaskState.Paused)],
            TrayState.Error => [Task("t1", TaskState.Done), Task("t2", TaskState.Error, error: "Name enthält ungültige Zeichen"), Task("t3", TaskState.Disabled, error: "Bitte erneut bei Büro anmelden.")],
            TrayState.Disconnected => [Task("t1", TaskState.Connecting), Task("t2", TaskState.Done), Task("t3", TaskState.Done)],
            _ => [Task("t1", TaskState.Done), Task("t2", TaskState.Done), Task("t3", TaskState.Done)],
        },
        hasNotifications: false);

    private static TaskStatus Task(string id, TaskState state, string? current = null, string? error = null) =>
        new(id, state, current is null ? 0 : 1, current, [], error, Now);

    public Task<OverallStatus> GetStatusAsync(CancellationToken ct) => System.Threading.Tasks.Task.FromResult(Status(TrayState.UpToDate));

    public Task<IReadOnlyList<TaskActivity>> GetRecentActivityAsync(int limit, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<IReadOnlyList<TaskActivity>>([.. Activity.Take(limit)]);

    public Task PauseAsync(string? taskId, CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

    public Task ResumeAsync(string? taskId, CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

    public async IAsyncEnumerable<OverallStatus> WatchStatusAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var status in Channel.CreateUnbounded<OverallStatus>().Reader.ReadAllAsync(ct))
        {
            yield return status;
        }
    }

    public Task<IReadOnlyList<TaskSummary>> GetTasksAsync(CancellationToken ct) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<TaskSummary>>(Tasks);

    public Task<IReadOnlyList<ConnectionSummary>> GetConnectionsAsync(CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<IReadOnlyList<ConnectionSummary>>(Connections);

    public Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(new SignInOutcome("c1"));

    public Task<IReadOnlyList<ServerFolder>> BrowseAsync(string connectionId, string? spaceId, string path, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<IReadOnlyList<ServerFolder>>(spaceId is null
            ? [new("s1", string.Empty, "My Drive", true, true), new("s2", string.Empty, "Familie", true, true), new("s3", string.Empty, "Team Folder", true, false)]
            : [new(spaceId, "Dokumente", "Dokumente", false, true), new(spaceId, "Fotos", "Fotos", false, true), new(spaceId, "Projekte", "Projekte", false, true)]);

    public Task<IReadOnlyList<TaskRuleViolation>> CheckLocalFolderAsync(string localPath, bool onDemand, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<IReadOnlyList<TaskRuleViolation>>([]);

    public Task<TaskOutcome> CreateTaskAsync(NewTaskRequest request, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(new TaskOutcome("t9", []));

    public Task DeleteTaskAsync(string taskId, CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

    public Task<ClientSettings> GetSettingsAsync(CancellationToken ct) => System.Threading.Tasks.Task.FromResult(new ClientSettings());

    public Task SaveSettingsAsync(ClientSettings settings, CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

    public Task<SyncTaskDefinition?> GetTaskAsync(string taskId, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<SyncTaskDefinition?>(new SyncTaskDefinition(
            taskId, "c1", "s1", RelativePath.Root, @"C:\Users\Beispiel\SyncDrive",
            Filters: new TaskFilterSettings(2L * 1024 * 1024 * 1024, ["Thumbs.db"], ["iso", "tmp"], [RelativePath.Parse("Fotos")])));

    public Task<TaskOutcome> UpdateTaskAsync(SyncTaskDefinition task, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(new TaskOutcome(task.Id, []));

    public Task<SignInOutcome> UpdateConnectionAsync(string connectionId, SignInRequest request, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(new SignInOutcome(connectionId));

    public Task<IReadOnlyList<TaskUsage>> GetUsageAsync(CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<IReadOnlyList<TaskUsage>>(
            [new("t1", 1_610_612_736, 1200), new("t2", 48_318_382_080, 9800), new("t3", 734_003_200, 310)]);

    public Task SetProxyPasswordAsync(string? password, CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

    public Task<IReadOnlyList<ServerNotification>> GetNotificationsAsync(CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<IReadOnlyList<ServerNotification>>(
        [
            new("n1", "c1", "Heim-NAS", "shared", Now.AddMinutes(-20), "ben", "Familie", string.Empty, false),
            new("n2", "c2", "Büro", "shared", Now.AddHours(-3), "carla", "Projekte", string.Empty, true),
        ]);

    public Task MarkNotificationsReadAsync(CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

    public Task<PathInfo?> GetPathInfoAsync(string localPath, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<PathInfo?>(new PathInfo("t1", "Angebote/Angebot.docx", false, PathStatus.UpToDate, false));

    public Task<IReadOnlyList<FileVersion>> ListVersionsAsync(string localPath, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<IReadOnlyList<FileVersion>>(
        [
            new(12, 48_213, Now.AddMinutes(-3), Now.AddMinutes(-3), "anna", true),
            new(9, 47_880, Now.AddHours(-26), Now.AddHours(-26), "anna", false),
            new(5, 31_002, Now.AddDays(-4), Now.AddDays(-4), "ben", false),
        ]);

    public Task<ShellResult> SaveVersionAsAsync(string localPath, long revision, string targetPath, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(ShellResult.Ok);

    public Task<ShellResult> RestoreVersionAsync(string localPath, long revision, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(ShellResult.Ok);

    public Task<ShellResult> SetFolderSyncedAsync(string localPath, bool synced, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(ShellResult.Ok);

    public ValueTask DisposeAsync()
    {
        Disconnected?.Invoke();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Oberflächendienste für Bilder: nichts öffnet sich, Dialoge antworten neutral.</summary>
internal sealed class SilentUi : IUiServices
{
    public void Post(Action action) => action();

    public Task<string?> PickLocalFolderAsync(string? suggestion) => System.Threading.Tasks.Task.FromResult<string?>(suggestion);

    public Task<(string SpaceId, string Path, string DisplayPath)?> PickServerFolderAsync(ServerFolderPickerViewModel picker) =>
        System.Threading.Tasks.Task.FromResult<(string, string, string)?>(("s1", "Dokumente", "My Drive/Dokumente"));

    public Task ShowMessageAsync(string message) => System.Threading.Tasks.Task.CompletedTask;

    public Task<bool> ConfirmAsync(string message) => System.Threading.Tasks.Task.FromResult(false);

    public void OpenFolder(string path)
    {
    }

    public void OpenMainWindow(MainPage page)
    {
    }

    public void OpenWizard()
    {
    }

    public void ShowAbout()
    {
    }

    public void ShowToast(string title, string message)
    {
    }

    public Task ShowEditConnectionAsync(EditConnectionViewModel dialog) => System.Threading.Tasks.Task.CompletedTask;

    public Task ShowSyncRulesAsync(SyncRulesViewModel dialog) => System.Threading.Tasks.Task.CompletedTask;

    public Task ShowVersionsAsync(VersionsViewModel dialog) => System.Threading.Tasks.Task.CompletedTask;

    public Task<string?> PickSaveFileAsync(string suggestedName, string? folder) => System.Threading.Tasks.Task.FromResult<string?>(null);

    public void Quit()
    {
    }
}
