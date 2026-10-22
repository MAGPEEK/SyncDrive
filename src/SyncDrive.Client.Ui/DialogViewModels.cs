using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.Ui;

/// <summary>
/// „Edit Connection“: Adresse und Benutzer ändern und neu anmelden (z. B. nach Adresswechsel oder entzogenem Gerät). Ablauf wie Seite 1 des
/// Assistenten: Prüfen sperrt den Dialog, ein unbekanntes Zertifikat wird mit Fingerabdruck nachgefragt.
/// </summary>
public sealed partial class EditConnectionViewModel(IClientSetup setup, ConnectionSummary connection, Texts texts) : ObservableObject
{
    [ObservableProperty]
    private string _address = connection.ServerAddress;

    [ObservableProperty]
    private string _userName = connection.UserName;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FingerprintText))]
    private string? _certificateFingerprint;

    public event Action<bool>? Finished;

    public Texts Texts { get; } = texts;

    public string Title => $"{Texts.EditConnection} – {connection.DisplayName}";

    public string FingerprintText => Fingerprints.Grouped(CertificateFingerprint);

    [RelayCommand]
    private Task SaveAsync() => SignInAsync(null);

    [RelayCommand]
    private Task TrustCertificateAsync() => SignInAsync(CertificateFingerprint);

    [RelayCommand]
    private void RejectCertificate() => CertificateFingerprint = null;

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(false);

    private async Task SignInAsync(string? trusted)
    {
        Error = null;
        IsBusy = true;
        try
        {
            var outcome = await setup.UpdateConnectionAsync(connection.Id, new SignInRequest(Address, UserName, Password, trusted), CancellationToken.None);
            CertificateFingerprint = null;
            if (outcome.Succeeded)
            {
                Password = string.Empty;
                Finished?.Invoke(true);
                return;
            }

            if (outcome.ErrorCode == SetupErrors.UntrustedCertificate)
            {
                CertificateFingerprint = outcome.CertificateSha256;
            }
            else
            {
                Error = outcome.ErrorCode switch
                {
                    SetupErrors.SignInFailed => Texts.ErrorSignIn,
                    SetupErrors.TooManyAttempts => Texts.ErrorTooManyAttempts,
                    SetupErrors.InvalidAddress => Texts.ErrorAddress,
                    _ => Texts.ErrorUnreachable,
                };
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public static class Fingerprints
{
    /// <summary>Fingerabdruck in Vierergruppen, damit man ihn mit dem auf dem Server vergleichen kann.</summary>
    public static string Grouped(string? fingerprint) => fingerprint is { } f
        ? string.Join(' ', Enumerable.Range(0, (f.Length + 3) / 4).Select(i => f.Substring(i * 4, Math.Min(4, f.Length - (i * 4)))))
        : string.Empty;
}

/// <summary>
/// „Sync Rules“ mit den Reitern Folder (welche Unterordner synchronisiert werden), File Filter (Größe, Namen, Endungen) und Sync Mode
/// (Richtung, Gelöschtes behalten, Konsistenzprüfung). Speichern startet die Aufgabe mit den neuen Regeln neu.
/// </summary>
public sealed partial class SyncRulesViewModel : ObservableObject
{
    private readonly IClientSetup _setup;
    private readonly IUiServices _ui;
    private readonly SyncTaskDefinition _task;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUploadOnly), nameof(TwoWay), nameof(DownloadOnly), nameof(UploadOnly))]
    private SyncDirection _direction;

    [ObservableProperty]
    private bool _keepLocallyDeleted;

    [ObservableProperty]
    private decimal _maxFileSizeMb;

    [ObservableProperty]
    private string _excludedExtensions;

    [ObservableProperty]
    private string _excludedNames;

    [ObservableProperty]
    private bool _isBusy;

    public SyncRulesViewModel(IClientSetup setup, IUiServices ui, Texts texts, SyncTaskDefinition task)
    {
        _setup = setup;
        _ui = ui;
        _task = task;
        Texts = texts;
        var filters = task.Filters ?? new TaskFilterSettings();
        _direction = task.Direction;
        _keepLocallyDeleted = task.KeepLocallyDeletedOnServer;
        _maxFileSizeMb = filters.MaxFileSizeBytes / (1024m * 1024m);
        _excludedExtensions = string.Join(", ", filters.ExcludedExtensions ?? []);
        _excludedNames = string.Join(", ", filters.ExcludedNames ?? []);
        Excluded = [.. (filters.ExcludedFolders ?? []).Select(f => f.Value)];
    }

    public event Action<bool>? Finished;

    public Texts Texts { get; }

    public string Title => $"{Texts.SyncRules} – {_task.LocalPath}";

    /// <summary>Unterordner (relativ zum Aufgabenordner), die nicht synchronisiert werden.</summary>
    public HashSet<string> Excluded { get; }

    public ObservableCollection<SelectableFolderNode> Folders { get; } = [];

    public bool IsUploadOnly => Direction == SyncDirection.UploadOnly;

    public bool TwoWay
    {
        get => Direction == SyncDirection.TwoWay;
        set => Set(value, SyncDirection.TwoWay);
    }

    public bool DownloadOnly
    {
        get => Direction == SyncDirection.DownloadOnly;
        set => Set(value, SyncDirection.DownloadOnly);
    }

    public bool UploadOnly
    {
        get => Direction == SyncDirection.UploadOnly;
        set => Set(value, SyncDirection.UploadOnly);
    }

    /// <summary>Oberste Ebene des Ordnerbaums (Inhalt des Aufgabenordners auf dem Server).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        Folders.Clear();
        foreach (var node in await SelectableFolderNode.ChildrenAsync(_setup, _task, this, _task.RemotePath.Value, parentChecked: true, ct))
        {
            Folders.Add(node);
        }
    }

    public SyncTaskDefinition ToTask()
    {
        static List<string> Split(string text) =>
            [.. text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e.TrimStart('*', '.')).Where(e => e.Length > 0)];

        var filters = new TaskFilterSettings(
            (long)(MaxFileSizeMb * 1024 * 1024),
            Split(ExcludedNames.Replace("*.", string.Empty, StringComparison.Ordinal)),
            Split(ExcludedExtensions),
            [.. Excluded.Order(StringComparer.Ordinal).Select(RelativePath.Parse)]);
        return _task with
        {
            Direction = Direction,
            KeepLocallyDeletedOnServer = KeepLocallyDeleted && IsUploadOnly,
            AdvancedConsistencyCheck = true, // keine Option mehr: wer eine Aufgabe ändert, bekommt die genaue Prüfung
            Filters = filters,
        };
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var outcome = await _setup.UpdateTaskAsync(ToTask(), CancellationToken.None);
            if (outcome.Succeeded)
            {
                Finished?.Invoke(true);
                return;
            }

            await _ui.ShowMessageAsync(outcome.Violations.Count > 0 ? Texts.ViolationText(outcome.Violations[0]) : outcome.Message ?? Texts.ErrorUnreachable);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(false);

    private void Set(bool selected, SyncDirection direction)
    {
        if (selected)
        {
            Direction = direction;
        }
    }
}

/// <summary>Ordner im Reiter „Folder“: Häkchen = wird synchronisiert. Ohne Häkchen am Eltern sind die Kinder mit ausgeschlossen.</summary>
public sealed partial class SelectableFolderNode : ObservableObject
{
    private readonly IClientSetup _setup;
    private readonly SyncTaskDefinition _task;
    private readonly SyncRulesViewModel _owner;
    private readonly string _serverPath;
    private bool _loaded;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isExpanded;

    private SelectableFolderNode(IClientSetup setup, SyncTaskDefinition task, SyncRulesViewModel owner, string serverPath, string name, bool parentChecked)
    {
        _setup = setup;
        _task = task;
        _owner = owner;
        _serverPath = serverPath;
        Name = name;
        RelativeToTask = Relative(task, serverPath);
        _isEnabled = parentChecked;
        _isChecked = parentChecked && !owner.Excluded.Contains(RelativeToTask);
        Children.Add(new SelectableFolderNode(name: "…"));
    }

    private SelectableFolderNode(string name)
    {
        _setup = null!;
        _task = null!;
        _owner = null!;
        _serverPath = string.Empty;
        Name = name;
        RelativeToTask = string.Empty;
        _loaded = true;
    }

    public string Name { get; }

    /// <summary>Pfad relativ zum Aufgabenordner, wie ihn der Filter erwartet.</summary>
    public string RelativeToTask { get; }

    public ObservableCollection<SelectableFolderNode> Children { get; } = [];

    public static async Task<List<SelectableFolderNode>> ChildrenAsync(
        IClientSetup setup, SyncTaskDefinition task, SyncRulesViewModel owner, string serverPath, bool parentChecked, CancellationToken ct)
    {
        var folders = await setup.BrowseAsync(task.ConnectionId, task.SpaceId, serverPath, ct);
        return [.. folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new SelectableFolderNode(setup, task, owner, f.Path, f.Name, parentChecked))];
    }

    public async Task LoadChildrenAsync()
    {
        _loaded = true;
        var children = await ChildrenAsync(_setup, _task, _owner, _serverPath, IsChecked, CancellationToken.None);
        Children.Clear();
        foreach (var child in children)
        {
            Children.Add(child);
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            _ = LoadChildrenAsync();
        }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_owner is null)
        {
            return;
        }

        if (value)
        {
            _owner.Excluded.Remove(RelativeToTask);
        }
        else
        {
            _owner.Excluded.Add(RelativeToTask);
            _owner.Excluded.RemoveWhere(e => e.StartsWith(RelativeToTask + "/", StringComparison.Ordinal)); // Kinder sind mit ausgeschlossen
        }

        foreach (var child in Children.Where(c => c._owner is not null))
        {
            child.IsEnabled = value;
            child.IsChecked = value && !_owner.Excluded.Contains(child.RelativeToTask);
        }
    }

    private static string Relative(SyncTaskDefinition task, string serverPath) =>
        task.RemotePath.IsRoot ? serverPath : serverPath[(task.RemotePath.Value.Length + 1)..];
}

/// <summary>Benachrichtigung in Panel und Seite „Notifications“: „anna shared Familie“ (Name und Ziel als Links) und Servername.</summary>
public sealed record NotificationItemViewModel(string Actor, string Verb, string Target, string Server, string Time, bool Unread)
{
    public static NotificationItemViewModel From(ServerNotification n, Texts texts, DateTimeOffset now)
    {
        var target = n.Path is { Length: > 0 } path ? RelativePath.Parse(path).Name : n.SpaceName ?? string.Empty;
        var verb = n.Type == "shared" ? texts.NotificationShared : n.Type;
        return new(n.Actor ?? "?", verb, target, n.ServerName, Presentation.RelativeTime(n.CreatedAt, now, texts), !n.Read);
    }
}

/// <summary>Seite „Notifications“: Filter (alle/ungelesen), Suche, Liste mit Server-Spalte.</summary>
public sealed partial class NotificationsViewModel(ShellViewModel shell) : ObservableObject
{
    public void RaiseAll() => OnPropertyChanged(string.Empty);

    private List<NotificationItemViewModel> _all = [];

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    public Texts Texts => shell.Texts;

    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    public async Task LoadAsync()
    {
        if (shell.Client is not { } client)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            _all = [.. (await client.GetNotificationsAsync(CancellationToken.None)).Select(n => NotificationItemViewModel.From(n, Texts, now))];
            Apply();
            if (_all.Any(n => n.Unread))
            {
                await client.MarkNotificationsReadAsync(CancellationToken.None);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Daemon nicht erreichbar
        }
    }

    partial void OnSearchChanged(string value) => Apply();

    private void Apply()
    {
        Items.Clear();
        foreach (var item in _all.Where(n => Search.Length == 0
            || $"{n.Actor} {n.Target} {n.Server}".Contains(Search, StringComparison.CurrentCultureIgnoreCase)))
        {
            Items.Add(item);
        }

        IsEmpty = Items.Count == 0;
    }
}

public static class Sizes
{
    /// <summary>„1.4 GB used“: Größe in der passenden Einheit.</summary>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
