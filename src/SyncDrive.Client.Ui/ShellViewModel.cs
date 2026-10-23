using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Status;

namespace SyncDrive.Client.Ui;

/// <summary>
/// Rückgrat der Oberfläche: hält die Verbindung zum Daemon (startet ihn bei Bedarf, verbindet nach Abbruch neu), zeigt den Gesamtzustand im
/// Tray (Symbol, Tooltip, Menü „Pause/Resume“) und versorgt Panel und Hauptfenster.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivityRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task<IDaemonClient>> _connect;
    private readonly Action _launchDaemon;
    private readonly IUiServices _ui;
    private readonly TimeProvider _clock;
    private readonly string _logsFolder;
    private DateTimeOffset _lastActivityRefresh;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateWord), nameof(StateColor), nameof(Tooltip), nameof(PauseMenuText), nameof(IsPaused))]
    private TrayState _trayState = TrayState.Disconnected;

    [ObservableProperty]
    private bool _hasNotifications;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip), nameof(StateWord))]
    private bool _isConnected;

    public ShellViewModel(
        Func<CancellationToken, Task<IDaemonClient>> connect, Action launchDaemon, IUiServices ui, Texts texts, string logsFolder, TimeProvider? clock = null)
    {
        _connect = connect;
        _launchDaemon = launchDaemon;
        _ui = ui;
        _logsFolder = logsFolder;
        _clock = clock ?? TimeProvider.System;
        Texts = texts;
        Panel = new PanelViewModel(this, ui, texts, _clock);
    }

    /// <summary>Neuer Gesamtstand (für Hauptfenster und Panel), im UI-Thread.</summary>
    public event Action<OverallStatus>? StatusChanged;

    /// <summary>Verbindung hergestellt oder verloren, im UI-Thread.</summary>
    public event Action? ConnectionChanged;

    /// <summary>Texte in der eingestellten Sprache; ein Wechsel meldet alle Eigenschaften neu (Bindungen aktualisieren sich).</summary>
    public Texts Texts { get; private set; }

    /// <summary>Zuletzt bekannte Einstellungen (Sprache, Desktop-Benachrichtigungen, schlichtes Tray-Symbol).</summary>
    public App.Settings.ClientSettings Settings { get; private set; } = new();

    public PanelViewModel Panel { get; }

    /// <summary>Übernimmt Einstellungen; eine andere Sprache gilt sofort.</summary>
    public void ApplySettings(App.Settings.ClientSettings settings)
    {
        Settings = settings;
        var texts = Texts.For(settings.Language);
        if (!ReferenceEquals(texts, Texts))
        {
            Texts = texts;
            OnPropertyChanged(string.Empty);
        }
        else
        {
            OnPropertyChanged(nameof(Settings)); // z. B. Tray-Symbol: schlicht oder farbig
        }
    }

    /// <summary>Desktop-Benachrichtigung zeigen, wenn eingestellt.</summary>
    internal void Toast(string message)
    {
        if (Settings.ShowDesktopNotifications)
        {
            _ui.Post(() => _ui.ShowToast(Texts.AppName, message));
        }
    }

    public IDaemonClient? Client { get; private set; }

    public OverallStatus? Status { get; private set; }

    public bool IsPaused => TrayState == TrayState.Paused;

    public string StateWord => IsConnected ? Presentation.Word(TrayState, Texts) : Texts.DaemonNotRunning;

    public StatusColor StateColor => Presentation.Color(TrayState);

    public string Tooltip => $"{Texts.AppName} – {StateWord}";

    public string PauseMenuText => IsPaused ? Texts.MenuResume : Texts.MenuPause;

    /// <summary>Verbinden, beobachten, bei Abbruch neu verbinden – bis <paramref name="ct"/> endet.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IDaemonClient client;
            try
            {
                client = await _connect(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _ui.Post(SetDisconnected);
                _launchDaemon();
                if (!await DelayAsync(RetryDelay, ct))
                {
                    return;
                }

                continue;
            }

            Client = client;
            App.Settings.ClientSettings? settings = null;
            try
            {
                settings = await client.GetSettingsAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Einstellungen später; die Verbindung zählt
            }

            _ui.Post(() =>
            {
                if (settings is not null)
                {
                    ApplySettings(settings);
                }

                IsConnected = true;
                ConnectionChanged?.Invoke();
            });
            try
            {
                await foreach (var status in client.WatchStatusAsync(ct))
                {
                    _ui.Post(() => Apply(status));
                    // Während des Abgleichs höchstens einmal je Intervall; der Abschluss immer, sonst fehlen die letzten Dateien in Liste und Meldung
                    if (_clock.GetUtcNow() - _lastActivityRefresh >= ActivityRefreshInterval || status.Tray != TrayState.Syncing)
                    {
                        _lastActivityRefresh = _clock.GetUtcNow();
                        await Panel.RefreshAsync(ct);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Verbindung abgerissen: unten aufräumen und neu verbinden
            }

            Client = null;
            await client.DisposeAsync();
            _ui.Post(SetDisconnected);
            if (!await DelayAsync(RetryDelay, ct))
            {
                return;
            }
        }
    }

    /// <summary>Bestehende Verbindung übernehmen, ohne Beobachtungsschleife (Vorschau-Bilder, Tests).</summary>
    public void UseClient(IDaemonClient client)
    {
        Client = client;
        IsConnected = true;
        ConnectionChanged?.Invoke();
    }

    /// <summary>Stand anwenden (im UI-Thread), wie er sonst aus der Beobachtung kommt.</summary>
    public void ApplyStatus(OverallStatus status) => Apply(status);

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (Client is not { } client)
        {
            return;
        }

        if (IsPaused)
        {
            await client.ResumeAsync(null, CancellationToken.None);
        }
        else
        {
            await client.PauseAsync(null, CancellationToken.None);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _ui.OpenMainWindow(MainPage.GlobalSettings);

    [RelayCommand]
    private void OpenMainWindow() => _ui.OpenMainWindow(MainPage.SyncTasks);

    [RelayCommand]
    private void About() => _ui.ShowAbout();

    /// <summary>Fehlersuche: öffnet den Ordner mit dem Daemon-Log.</summary>
    [RelayCommand]
    private void Troubleshoot() => _ui.OpenFolder(_logsFolder);

    [RelayCommand]
    private void Exit() => _ui.Quit();

    private void Apply(OverallStatus status)
    {
        Status = status;
        TrayState = status.Tray;
        HasNotifications = status.HasNotifications;
        StatusChanged?.Invoke(status);
    }

    private void SetDisconnected()
    {
        var changed = IsConnected;
        IsConnected = false;
        TrayState = TrayState.Disconnected;
        if (changed)
        {
            ConnectionChanged?.Invoke();
        }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _clock, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Eintrag in „Recently Changed“: Dateityp-Symbol, Name, darunter „Aktion / relative Zeit“; Gelöschtes ausgegraut.</summary>
public sealed record ActivityItemViewModel(string Name, string Detail, FileKind Kind, bool Dimmed, string? LocalPath)
{
    public IRelayCommand? Open { get; init; }
}

/// <summary>Panel am Tray-Symbol: Kopf (Symbol + Wort + Farbe), Reiter „Recently Changed“ und „Notifications“, Fußleiste mit Schnellzugriffen.</summary>
public sealed partial class PanelViewModel : ObservableObject
{
    private const int RecentCount = 30;
    private readonly ShellViewModel _shell;
    private readonly IUiServices _ui;
    private readonly TimeProvider _clock;
    private IReadOnlyList<TaskSummary> _tasks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotifications))]
    private bool _showRecent = true;

    [ObservableProperty]
    private bool _hasRecent;

    [ObservableProperty]
    private bool _hasNotificationItems;

    private HashSet<string>? _seenNotifications;
    private DateTimeOffset? _seenActivity;

    public PanelViewModel(ShellViewModel shell, IUiServices ui, Texts texts, TimeProvider clock)
    {
        _shell = shell;
        _ui = ui;
        _clock = clock;
        _ = texts;
        shell.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName))
            {
                OnPropertyChanged(string.Empty); // Sprache gewechselt
                _ = RefreshAsync(CancellationToken.None);
            }
        };
    }

    public Texts Texts => _shell.Texts;

    public ShellViewModel Shell => _shell;

    public bool ShowNotifications => !ShowRecent;

    /// <summary>„⋯“ mit Pause, Fehlersuche, Beenden: unter macOS, wo das Symbol in der Menüleiste kein eigenes Menü hat.</summary>
    public bool ShowMoreMenu { get; init; } = OperatingSystem.IsMacOS();

    public ObservableCollection<ActivityItemViewModel> Recent { get; } = [];

    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = [];

    /// <summary>Lädt Aufgaben, Verlauf und Benachrichtigungen neu (beim Öffnen des Panels und nach Statusänderungen); Neues wird gemeldet.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (_shell.Client is not { } client)
        {
            return;
        }

        try
        {
            _tasks = await client.GetTasksAsync(ct);
            var activity = await client.GetRecentActivityAsync(RecentCount, ct);
            var notifications = await client.GetNotificationsAsync(ct);
            var now = _clock.GetUtcNow();
            var items = activity.Select(a => ToItem(a, now)).ToList();
            var notes = notifications.Select(n => NotificationItemViewModel.From(n, Texts, now)).ToList();
            AnnounceNews(activity, notifications);
            _ui.Post(() =>
            {
                Recent.Clear();
                foreach (var item in items)
                {
                    Recent.Add(item);
                }

                Notifications.Clear();
                foreach (var note in notes)
                {
                    Notifications.Add(note);
                }

                HasRecent = Recent.Count > 0;
                HasNotificationItems = Notifications.Count > 0;
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Daemon gerade weg; die Shell verbindet neu
        }
    }

    [RelayCommand]
    private void SelectRecent() => ShowRecent = true;

    /// <summary>Reiter „Notifications“: zeigen und alles als gelesen markieren (der rote Punkt verschwindet).</summary>
    [RelayCommand]
    private async Task SelectNotificationsAsync()
    {
        ShowRecent = false;
        if (_shell.Client is { } client && Notifications.Any(n => n.Unread))
        {
            try
            {
                await client.MarkNotificationsReadAsync(CancellationToken.None);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // nächster Versuch beim nächsten Öffnen
            }
        }
    }

    /// <summary>Neue Benachrichtigungen und neue Konfliktkopien als Desktop-Benachrichtigung; beim ersten Laden nur merken.</summary>
    private void AnnounceNews(IReadOnlyList<TaskActivity> activity, IReadOnlyList<ServerNotification> notifications)
    {
        var first = _seenNotifications is null;
        _seenNotifications ??= [];
        foreach (var note in notifications.Where(n => !n.Read && _seenNotifications.Add(n.Id)))
        {
            if (!first)
            {
                var item = NotificationItemViewModel.From(note, Texts, _clock.GetUtcNow());
                _shell.Toast($"{item.Actor} {item.Verb} {item.Target}");
            }
        }

        var newest = activity.Count > 0 ? activity.Max(a => a.Entry.At) : (DateTimeOffset?)null;
        if (_seenActivity is { } seen)
        {
            foreach (var conflict in activity.Where(a => a.Entry.At > seen && a.Entry.Action == App.Ports.ActivityAction.Conflict))
            {
                _shell.Toast($"{Texts.Conflict}: {conflict.Entry.Path.Name}");
            }

            // Neu übertragene Dateien: eine nennt sie beim Namen, mehrere fasst eine Meldung zusammen
            var synced = activity.Where(a => a.Entry is { IsDirectory: false, Result: App.Ports.ActivityResult.Done } && a.Entry.At > seen
                && a.Entry.Action is App.Ports.ActivityAction.Upload or App.Ports.ActivityAction.Download).ToList();
            if (synced.Count == 1)
            {
                _shell.Toast($"{synced[0].Entry.Path.Name}: {Presentation.Action(synced[0].Entry, Texts)}");
            }
            else if (synced.Count > 1)
            {
                _shell.Toast(string.Format(System.Globalization.CultureInfo.CurrentCulture, Texts.FilesSynced, synced.Count));
            }
        }

        _seenActivity = newest ?? _seenActivity ?? DateTimeOffset.MinValue;
    }

    /// <summary>Ordner-Knopf: öffnet den lokalen Ordner der ersten Aufgabe (wie die Desktopverknüpfung).</summary>
    [RelayCommand]
    private void OpenSyncFolder()
    {
        if (_tasks.Count > 0)
        {
            _ui.OpenFolder(_tasks[0].LocalPath);
        }
        else
        {
            _ui.OpenWizard();
        }
    }

    [RelayCommand]
    private void OpenSettings() => _ui.OpenMainWindow(MainPage.GlobalSettings);

    [RelayCommand]
    private void OpenMainWindow() => _ui.OpenMainWindow(MainPage.SyncTasks);

    [RelayCommand]
    private void Help() => _ui.ShowAbout();

    private ActivityItemViewModel ToItem(TaskActivity activity, DateTimeOffset now)
    {
        var entry = activity.Entry;
        var root = _tasks.FirstOrDefault(t => t.TaskId == activity.TaskId)?.LocalPath;
        var full = root is null ? null : Path.Combine(root, entry.Path.Value.Replace('/', Path.DirectorySeparatorChar));
        var folder = full is null ? null : entry.IsDirectory ? full : Path.GetDirectoryName(full);
        var detail = $"{Presentation.Action(entry, Texts)} / {Presentation.RelativeTime(entry.At, now, Texts)}";
        return new ActivityItemViewModel(
            entry.Path.Name, detail, Presentation.KindOf(entry.Path.Name, entry.IsDirectory), entry.Action == App.Ports.ActivityAction.Delete, full)
        {
            Open = folder is null ? null : new RelayCommand(() => _ui.OpenFolder(folder)),
        };
    }
}
