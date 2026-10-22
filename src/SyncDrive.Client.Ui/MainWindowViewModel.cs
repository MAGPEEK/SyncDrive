using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Status;

namespace SyncDrive.Client.Ui;

/// <summary>Hauptfenster: linke Navigation mit fünf festen Seiten, rechts die gewählte Seite.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncTasks), nameof(IsBackup), nameof(IsLogs), nameof(IsNotifications), nameof(IsSettings))]
    private MainPage _page = MainPage.SyncTasks;

    public MainWindowViewModel(ShellViewModel shell, IUiServices ui)
    {
        _shell = shell;
        SyncTasks = new SyncTasksViewModel(shell, ui);
        Logs = new LogsViewModel(shell);
        Settings = new SettingsViewModel(shell);
        Notifications = new NotificationsViewModel(shell);
        shell.StatusChanged += SyncTasks.ApplyStatus;
        shell.ConnectionChanged += () => _ = RefreshAsync();
        shell.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName))
            {
                // Sprache gewechselt: alle Texte und Listen neu
                OnPropertyChanged(string.Empty);
                SyncTasks.RaiseAll();
                Logs.RaiseAll();
                Settings.RaiseAll();
                Notifications.RaiseAll();
                _ = RefreshAsync();
            }
        };
    }

    public Texts Texts => _shell.Texts;

    public SyncTasksViewModel SyncTasks { get; }

    public LogsViewModel Logs { get; }

    public SettingsViewModel Settings { get; }

    public NotificationsViewModel Notifications { get; }

    public bool IsSyncTasks => Page == MainPage.SyncTasks;

    public bool IsBackup => Page == MainPage.BackupTask;

    public bool IsLogs => Page == MainPage.Logs;

    public bool IsNotifications => Page == MainPage.Notifications;

    public bool IsSettings => Page == MainPage.GlobalSettings;

    public async Task RefreshAsync()
    {
        await SyncTasks.LoadAsync();
        await Logs.LoadAsync();
        await Settings.LoadAsync();
        await Settings.LoadUsageAsync();
        await Notifications.LoadAsync();
    }

    [RelayCommand]
    private async Task NavigateAsync(MainPage page)
    {
        Page = page;
        if (page == MainPage.Logs)
        {
            await Logs.LoadAsync();
        }
        else if (page == MainPage.Notifications)
        {
            await Notifications.LoadAsync();
        }
        else if (page == MainPage.GlobalSettings)
        {
            await Settings.LoadUsageAsync();
        }
    }

    partial void OnPageChanged(MainPage value) => _ = value == MainPage.SyncTasks ? SyncTasks.LoadAsync() : Task.CompletedTask;

    internal ShellViewModel Shell => _shell;
}

/// <summary>Servergruppe der Aufgabenliste: aufklappbar, Name fett und Adresse klein.</summary>
public sealed partial class ServerGroupViewModel(string name, string address) : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded = true;

    public string Name { get; } = name;

    public string Address { get; } = address;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
}

/// <summary>Aufgabenzeile: Ordnersymbol mit Zustandsabzeichen, Titel, „(Modus)“, Cloud-Hinweis bei On-Demand, lokaler Pfad als Link.</summary>
public sealed partial class TaskRowViewModel(TaskSummary task, Texts texts) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsPaused))]
    private TaskState _state = TaskState.Connecting;

    [ObservableProperty]
    private string? _detail;

    public TaskSummary Task { get; } = task;

    public string Title => Task.RemotePath.Length == 0 ? Task.SpaceName(texts) : Task.RemotePath.Split('/')[^1];

    public string ModeText => $"({Presentation.Direction(Task.Direction, texts)})";

    public string StateText => Presentation.Word(State, texts);

    public bool IsPaused => State == TaskState.Paused;
}

internal static class TaskSummaryExtensions
{
    /// <summary>Ohne Unterordner heißt die Aufgabe wie ihr Space; die Liste zeigt dann den letzten Teil des lokalen Ordners.</summary>
    public static string SpaceName(this TaskSummary task, Texts texts) =>
        task.LocalPath.TrimEnd('\\', '/').Split('\\', '/')[^1] is { Length: > 0 } name ? name : texts.AppName; // Windows- und Unix-Trenner
}

/// <summary>Seite „Sync Tasks“: Werkzeugleiste (Create, Pause/Resume, Delete, …) und Aufgaben nach Server gruppiert; ohne Aufgaben ein Leerzustand.</summary>
public sealed partial class SyncTasksViewModel(ShellViewModel shell, IUiServices ui) : ObservableObject
{
    public void RaiseAll() => OnPropertyChanged(string.Empty);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(PauseText))]
    [NotifyCanExecuteChangedFor(nameof(PauseOrResumeCommand), nameof(DeleteCommand), nameof(EditConnectionCommand), nameof(SyncRulesCommand))]
    private TaskRowViewModel? _selected;

    [ObservableProperty]
    private bool _isEmpty = true;

    public Texts Texts => shell.Texts;

    public ObservableCollection<ServerGroupViewModel> Groups { get; } = [];

    /// <summary>Für die Liste: Servergruppen-Köpfe und darunter (aufgeklappt) ihre Aufgaben, flach in einer Folge.</summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>Auswahl in der flachen Liste; nur Aufgabenzeilen sind auswählbar.</summary>
    public object? SelectedItem
    {
        get => Selected;
        set
        {
            if (value is TaskRowViewModel row)
            {
                Selected = row;
            }

            OnPropertyChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    public string PauseText => Selected?.IsPaused == true ? Texts.MenuResume : Texts.MenuPause;

    public async Task LoadAsync()
    {
        if (shell.Client is not { } client)
        {
            return;
        }

        IReadOnlyList<TaskSummary> tasks;
        try
        {
            tasks = await client.GetTasksAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }

        var selectedId = Selected?.Task.TaskId;
        Groups.Clear();
        foreach (var group in tasks.GroupBy(t => (t.ConnectionId, t.ServerName, t.ServerAddress)))
        {
            var server = new ServerGroupViewModel(group.Key.ServerName, group.Key.ServerAddress);
            foreach (var task in group)
            {
                server.Tasks.Add(new TaskRowViewModel(task, Texts));
            }

            Groups.Add(server);
        }

        RebuildItems();
        IsEmpty = Groups.Count == 0;
        if (shell.Status is { } status)
        {
            ApplyStatus(status);
        }

        Selected = Groups.SelectMany(g => g.Tasks).FirstOrDefault(t => t.Task.TaskId == selectedId);
    }

    public void ApplyStatus(OverallStatus status)
    {
        foreach (var row in Groups.SelectMany(g => g.Tasks))
        {
            if (status.Tasks.FirstOrDefault(s => s.TaskId == row.Task.TaskId) is { } task)
            {
                row.State = task.State;
                row.Detail = task.State is TaskState.Disabled or TaskState.Error ? task.LastError : task.CurrentPath;
            }
        }

        OnPropertyChanged(nameof(PauseText));
    }

    [RelayCommand]
    private void ToggleGroup(ServerGroupViewModel group)
    {
        group.IsExpanded = !group.IsExpanded;
        RebuildItems();
    }

    partial void OnSelectedChanged(TaskRowViewModel? value) => OnPropertyChanged(nameof(SelectedItem));

    private void RebuildItems()
    {
        Items.Clear();
        foreach (var group in Groups)
        {
            Items.Add(group);
            if (group.IsExpanded)
            {
                foreach (var row in group.Tasks)
                {
                    Items.Add(row);
                }
            }
        }
    }

    [RelayCommand]
    private void Create() => ui.OpenWizard();

    /// <summary>„Edit Connection“ für die Verbindung der gewählten Aufgabe; danach Liste neu (Aufgaben starten neu).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditConnectionAsync()
    {
        if (Selected is not { } row || shell.Client is not { } client)
        {
            return;
        }

        var connection = (await client.GetConnectionsAsync(CancellationToken.None)).FirstOrDefault(c => c.Id == row.Task.ConnectionId);
        if (connection is not null)
        {
            await ui.ShowEditConnectionAsync(new EditConnectionViewModel(client, connection, Texts));
            await LoadAsync();
        }
    }

    /// <summary>„Sync Rules“ der gewählten Aufgabe: Ordner, Dateifilter, Sync-Modus.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SyncRulesAsync()
    {
        if (Selected is not { } row || shell.Client is not { } client)
        {
            return;
        }

        if (await client.GetTaskAsync(row.Task.TaskId, CancellationToken.None) is { } task)
        {
            var rules = new SyncRulesViewModel(client, ui, Texts, task);
            await rules.LoadAsync(CancellationToken.None);
            await ui.ShowSyncRulesAsync(rules);
            await LoadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PauseOrResumeAsync()
    {
        if (Selected is not { } row || shell.Client is not { } client)
        {
            return;
        }

        if (row.IsPaused)
        {
            await client.ResumeAsync(row.Task.TaskId, CancellationToken.None);
        }
        else
        {
            await client.PauseAsync(row.Task.TaskId, CancellationToken.None);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row || shell.Client is not { } client || !await ui.ConfirmAsync(Texts.ConfirmDeleteTask))
        {
            return;
        }

        await client.DeleteTaskAsync(row.Task.TaskId, CancellationToken.None);
        Selected = null;
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenLocalFolder(TaskRowViewModel row) => ui.OpenFolder(row.Task.LocalPath);
}

public enum EventFilter
{
    All,
    Upload,
    Download,
    Delete,
    Conflict,
    Move,
    Unsynced,
}

/// <summary>Zeile der Seite „Logs“: Name (mittig gekürzt), Datei/Ordner, Datum und Uhrzeit, Ereignis.</summary>
public sealed record LogRow(string TaskId, string Name, string FullName, string Kind, string Time, string Event, ActivityEntry Entry);

/// <summary>Seite „Logs“: Filter „All tasks“ und „All events“, Suche, neueste Einträge oben.</summary>
public sealed partial class LogsViewModel(ShellViewModel shell) : ObservableObject
{
    public void RaiseAll() => OnPropertyChanged(string.Empty);

    private const int MaxEntries = 1000;
    private List<LogRow> _all = [];

    [ObservableProperty]
    private KeyValuePair<string, string> _taskFilter;

    [ObservableProperty]
    private EventFilter _eventFilter = EventFilter.All;

    [ObservableProperty]
    private string _search = string.Empty;

    public Texts Texts => shell.Texts;

    public ObservableCollection<KeyValuePair<string, string>> TaskOptions { get; } = [];

    public IReadOnlyList<EventFilter> EventOptions { get; } = Enum.GetValues<EventFilter>();

    /// <summary>Auswahlliste „All events“ mit Texten.</summary>
    public IReadOnlyList<KeyValuePair<EventFilter, string>> EventChoices => [.. EventOptions.Select(e => new KeyValuePair<EventFilter, string>(e, EventFilterText(e)))];

    public KeyValuePair<EventFilter, string> EventChoice
    {
        get => new(EventFilter, EventFilterText(EventFilter));
        set => EventFilter = value.Key;
    }

    public ObservableCollection<LogRow> Rows { get; } = [];

    public string EventFilterText(EventFilter filter) => filter switch
    {
        EventFilter.All => Texts.AllEvents,
        EventFilter.Upload => Texts.Uploaded,
        EventFilter.Download => Texts.Downloaded,
        EventFilter.Delete => Texts.Deleted,
        EventFilter.Conflict => Texts.Conflict,
        EventFilter.Move => Texts.Moved,
        _ => Texts.Unsynced,
    };

    public async Task LoadAsync()
    {
        if (shell.Client is not { } client)
        {
            return;
        }

        try
        {
            var tasks = await client.GetTasksAsync(CancellationToken.None);
            var activity = await client.GetRecentActivityAsync(MaxEntries, CancellationToken.None);
            var selected = TaskFilter.Key;
            TaskOptions.Clear();
            TaskOptions.Add(new(string.Empty, Texts.AllTasks));
            foreach (var task in tasks)
            {
                TaskOptions.Add(new(task.TaskId, new TaskRowViewModel(task, Texts).Title));
            }

            TaskFilter = TaskOptions.FirstOrDefault(o => o.Key == selected, TaskOptions[0]);
            _all = [.. activity.Select(a => new LogRow(
                a.TaskId,
                Presentation.MiddleEllipsis(a.Entry.Path.Name, 20),
                a.Entry.Path.Value,
                a.Entry.IsDirectory ? Texts.Folder : Texts.File,
                a.Entry.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                Presentation.Action(a.Entry, Texts),
                a.Entry))];
            Apply();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Daemon nicht erreichbar
        }
    }

    partial void OnTaskFilterChanged(KeyValuePair<string, string> value) => Apply();

    partial void OnEventFilterChanged(EventFilter value)
    {
        OnPropertyChanged(nameof(EventChoice));
        Apply();
    }

    partial void OnSearchChanged(string value) => Apply();

    private void Apply()
    {
        Rows.Clear();
        foreach (var row in _all.Where(Matches))
        {
            Rows.Add(row);
        }
    }

    private bool Matches(LogRow row) =>
        (string.IsNullOrEmpty(TaskFilter.Key) || row.TaskId == TaskFilter.Key)
        && (Search.Length == 0 || row.FullName.Contains(Search, StringComparison.CurrentCultureIgnoreCase))
        && EventFilter switch
        {
            EventFilter.All => true,
            EventFilter.Unsynced => row.Entry.Result != ActivityResult.Done,
            EventFilter.Upload => row.Entry.Action == ActivityAction.Upload,
            EventFilter.Download => row.Entry.Action == ActivityAction.Download,
            EventFilter.Delete => row.Entry.Action == ActivityAction.Delete,
            EventFilter.Conflict => row.Entry.Action == ActivityAction.Conflict,
            _ => row.Entry.Action == ActivityAction.Move,
        };
}

/// <summary>
/// „Global Settings“ mit den Reitern General, Notifications, Display, Proxy, Usage. Jede Änderung wird sofort gespeichert (der Daemon wendet sie
/// an, z. B. Autostart). Die Felder entsprechen eins zu eins <see cref="ClientSettings"/>.
/// </summary>
public sealed partial class SettingsViewModel(ShellViewModel shell) : ObservableObject
{
    public void RaiseAll() => OnPropertyChanged(string.Empty);

    private bool _loading;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _refetchOnRestart;

    [ObservableProperty]
    private bool _keepNewest;

    [ObservableProperty]
    private bool _renameDiscarded;

    [ObservableProperty]
    private string? _desktopShortcutFolder;

    [ObservableProperty]
    private string _language = "system";

    [ObservableProperty]
    private bool _showDesktopNotifications;

    [ObservableProperty]
    private bool _showTips;

    [ObservableProperty]
    private bool _minimalTrayIcon;

    [ObservableProperty]
    private bool _showContextMenu;

    [ObservableProperty]
    private bool _showOverlays;

    [ObservableProperty]
    private bool _showOverlaysForUnsynced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProxyManual))]
    private ProxyMode _proxyMode;

    [ObservableProperty]
    private string _proxyHost = string.Empty;

    [ObservableProperty]
    private decimal? _proxyPort;

    [ObservableProperty]
    private bool _proxyAuthentication;

    [ObservableProperty]
    private string _proxyUserName = string.Empty;

    public Texts Texts => shell.Texts;

    public bool IsProxyManual => ProxyMode == ProxyMode.Manual;

    public IReadOnlyList<KeyValuePair<string, string>> Languages => [new("system", Texts.LanguageSystem), new("en", "English"), new("de", "Deutsch")];

    public KeyValuePair<string, string> LanguageChoice
    {
        get => Languages.FirstOrDefault(l => l.Key == Language, Languages[0]);
        set => Language = value.Key ?? "system";
    }

    public ObservableCollection<string> ShortcutFolders { get; } = [];

    public ObservableCollection<ServerGroupViewModel> Usage { get; } = [];

    public async Task LoadAsync()
    {
        if (shell.Client is not { } client)
        {
            return;
        }

        try
        {
            var settings = await client.GetSettingsAsync(CancellationToken.None);
            var tasks = await client.GetTasksAsync(CancellationToken.None);
            _loading = true;
            ShortcutFolders.Clear();
            Usage.Clear();
            foreach (var task in tasks)
            {
                var title = new TaskRowViewModel(task, Texts).Title;
                ShortcutFolders.Add(title);
            }

            foreach (var group in tasks.GroupBy(t => (t.ServerName, t.ServerAddress)))
            {
                var server = new ServerGroupViewModel(group.Key.ServerName, group.Key.ServerAddress);
                foreach (var task in group)
                {
                    server.Tasks.Add(new TaskRowViewModel(task, Texts) { Detail = "—" });
                }

                Usage.Add(server);
            }

            StartWithWindows = settings.StartWithWindows;
            RefetchOnRestart = settings.OnRestart == OnRestartLocalDeletes.Refetch;
            KeepNewest = settings.Conflict == ConflictStrategy.NewerWins;
            RenameDiscarded = settings.RenameDiscarded;
            DesktopShortcutFolder = settings.DesktopShortcutFolder ?? ShortcutFolders.FirstOrDefault();
            Language = settings.Language;
            ShowDesktopNotifications = settings.ShowDesktopNotifications;
            ShowTips = settings.ShowTips;
            MinimalTrayIcon = settings.MinimalTrayIcon;
            ShowContextMenu = settings.ShowContextMenu;
            ShowOverlays = settings.ShowOverlays;
            ShowOverlaysForUnsynced = settings.ShowOverlaysForUnsynced;
            ProxyMode = settings.Proxy.Mode;
            ProxyHost = settings.Proxy.Host ?? string.Empty;
            ProxyPort = settings.Proxy.Port;
            ProxyAuthentication = settings.Proxy.UseAuthentication;
            ProxyUserName = settings.Proxy.UserName ?? string.Empty;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Daemon nicht erreichbar
        }
        finally
        {
            _loading = false;
        }
    }

    public ClientSettings ToSettings() => new()
    {
        StartWithWindows = StartWithWindows,
        OnRestart = RefetchOnRestart ? OnRestartLocalDeletes.Refetch : OnRestartLocalDeletes.PropagateDelete,
        Conflict = KeepNewest ? ConflictStrategy.NewerWins : ConflictStrategy.ServerWins,
        RenameDiscarded = RenameDiscarded,
        DesktopShortcutFolder = DesktopShortcutFolder,
        Language = Language,
        ShowDesktopNotifications = ShowDesktopNotifications,
        ShowTips = ShowTips,
        MinimalTrayIcon = MinimalTrayIcon,
        ShowContextMenu = ShowContextMenu,
        ShowOverlays = ShowOverlays,
        ShowOverlaysForUnsynced = ShowOverlaysForUnsynced,
        Proxy = new ProxySettings(
            ProxyMode, ProxyHost.Length == 0 ? null : ProxyHost, ProxyPort is { } port ? (int)port : null, ProxyAuthentication,
            ProxyUserName.Length == 0 ? null : ProxyUserName),
    };

    /// <summary>Für die Optionsfelder „neu laden / auch löschen“ und „neueste / Server“.</summary>
    public bool PropagateOnRestart
    {
        get => !RefetchOnRestart;
        set => RefetchOnRestart = !value;
    }

    public bool KeepServer
    {
        get => !KeepNewest;
        set => KeepNewest = !value;
    }

    public bool ProxyNone
    {
        get => ProxyMode == ProxyMode.None;
        set
        {
            if (value)
            {
                ProxyMode = ProxyMode.None;
            }
        }
    }

    public bool ProxyAuto
    {
        get => ProxyMode == ProxyMode.Auto;
        set
        {
            if (value)
            {
                ProxyMode = ProxyMode.Auto;
            }
        }
    }

    public bool ProxyManual
    {
        get => ProxyMode == ProxyMode.Manual;
        set
        {
            if (value)
            {
                ProxyMode = ProxyMode.Manual;
            }
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(RefetchOnRestart))
        {
            base.OnPropertyChanged(nameof(PropagateOnRestart));
        }
        else if (e.PropertyName == nameof(KeepNewest))
        {
            base.OnPropertyChanged(nameof(KeepServer));
        }
        else if (e.PropertyName == nameof(Language))
        {
            base.OnPropertyChanged(nameof(LanguageChoice));
        }
        else if (e.PropertyName == nameof(ProxyMode))
        {
            base.OnPropertyChanged(nameof(ProxyNone));
            base.OnPropertyChanged(nameof(ProxyAuto));
            base.OnPropertyChanged(nameof(ProxyManual));
        }

        if (!_loading && !string.IsNullOrEmpty(e.PropertyName) && e.PropertyName is not (nameof(PropagateOnRestart) or nameof(KeepServer) or nameof(ProxyNone) or nameof(ProxyAuto)
            or nameof(ProxyManual) or nameof(IsProxyManual) or nameof(LanguageChoice) or nameof(ProxyPassword)) && shell.Client is { } client)
        {
            _ = SaveAsync(client);
        }
    }

    private async Task SaveAsync(IClientSetup client)
    {
        var settings = ToSettings();
        shell.ApplySettings(settings); // Sprache und Benachrichtigungen gelten sofort
        try
        {
            await client.SaveSettingsAsync(settings, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Daemon nicht erreichbar: beim nächsten Öffnen wird neu geladen
        }
    }

    /// <summary>Proxy-Passwort: eigenes Feld, landet im Geheimnisspeicher des Daemons, nie in den Einstellungen.</summary>
    [ObservableProperty]
    private string _proxyPassword = string.Empty;

    [RelayCommand]
    private async Task SaveProxyPasswordAsync()
    {
        if (shell.Client is { } client)
        {
            await client.SetProxyPasswordAsync(ProxyPassword.Length == 0 ? null : ProxyPassword, CancellationToken.None);
            ProxyPassword = string.Empty;
        }
    }

    /// <summary>„Usage“: belegter Speicher je Aufgabe vom Server.</summary>
    public async Task LoadUsageAsync()
    {
        if (shell.Client is not { } client)
        {
            return;
        }

        try
        {
            var usage = (await client.GetUsageAsync(CancellationToken.None)).ToDictionary(u => u.TaskId);
            foreach (var row in Usage.SelectMany(g => g.Tasks))
            {
                row.Detail = usage.GetValueOrDefault(row.Task.TaskId)?.Bytes is { } bytes
                    ? string.Format(CultureInfo.CurrentCulture, Texts.UsedFormat, Sizes.Format(bytes))
                    : "—";
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Server nicht erreichbar: Striche bleiben
        }
    }
}
