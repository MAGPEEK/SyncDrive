using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Settings;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.Ui;

public enum WizardStep
{
    Connection,
    Folders,
    Options,
}

/// <summary>
/// Assistent „Create a new sync task“: Verbindung (frühere oder neue; die nicht gewählte Gruppe ist gesperrt),
/// Prüfen mit Sperre über der Seite, Ordner (Server oben, Computer unten; ohne eigene Wahl „SyncDrive“ im Benutzerordner), Optionen. Regelverstöße kommen
/// als Meldung mit Grund, sobald ein Ordner gewählt ist.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly IClientSetup _setup;
    private readonly IUiServices _ui;
    private readonly string _suggestedLocalFolder;
    private string? _connectionId;
    private string? _spaceId;
    private string? _remotePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(IsConnectionStep), nameof(IsFoldersStep), nameof(IsOptionsStep), nameof(CanGoBack), nameof(NextText))]
    private WizardStep _step = WizardStep.Connection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseNew))]
    private bool _useExisting;

    [ObservableProperty]
    private ConnectionSummary? _selectedConnection;

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FingerprintText))]
    private string? _certificateFingerprint;

    [ObservableProperty]
    private string _serverFolderText;

    [ObservableProperty]
    private string? _localFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUploadOnly))]
    private SyncDirection _direction = SyncDirection.TwoWay;

    [ObservableProperty]
    private bool _keepLocallyDeleted = true;

    [ObservableProperty]
    private decimal _maxFileSizeMb;

    [ObservableProperty]
    private string _excludedExtensions = string.Empty;

    [ObservableProperty]
    private bool _onDemand;

    /// <summary>
    /// Dateien auf Abruf gibt es bisher nur unter Windows (Cloud-Files-API); unter macOS folgen sie mit der File-Provider-Erweiterung
    /// (ADR-14, Stufe 2). Bis dahin zeigt der Assistent die Option dort nicht.
    /// </summary>
    public bool OnDemandAvailable { get; init; } = OperatingSystem.IsWindows();

    public WizardViewModel(IClientSetup setup, IUiServices ui, Texts texts, string suggestedLocalFolder)
    {
        _setup = setup;
        _ui = ui;
        _suggestedLocalFolder = suggestedLocalFolder;
        Texts = texts;
        _serverFolderText = texts.NotYetSelected;
    }

    /// <summary>Assistent beendet; true = Aufgabe angelegt.</summary>
    public event Action<bool>? Finished;

    public Texts Texts { get; }

    public ObservableCollection<ConnectionSummary> Connections { get; } = [];

    public bool HasConnections => Connections.Count > 0;

    public bool UseNew => !UseExisting;

    public bool IsConnectionStep => Step == WizardStep.Connection;

    public bool IsFoldersStep => Step == WizardStep.Folders;

    public bool IsOptionsStep => Step == WizardStep.Options;

    public bool IsUploadOnly => Direction == SyncDirection.UploadOnly;

    public bool CanGoBack => Step != WizardStep.Connection;

    public string NextText => Step == WizardStep.Options ? Texts.Done : Texts.Next;

    public string Heading => Step switch
    {
        WizardStep.Connection => Texts.WizardConnectionHeading,
        WizardStep.Folders => Texts.WizardFoldersHeading,
        _ => Texts.WizardOptionsHeading,
    };

    /// <summary>Ohne eigene Wahl der Standardordner, der beim Weiter angelegt wird.</summary>
    public string LocalFolderText => LocalFolder ?? string.Format(CultureInfo.CurrentCulture, Texts.DefaultLocalFolder, _suggestedLocalFolder);

    /// <summary>Fingerabdruck in Vierergruppen, damit man ihn mit dem auf dem Server vergleichen kann.</summary>
    public string FingerprintText => Fingerprints.Grouped(CertificateFingerprint);

    public IReadOnlyList<SyncDirection> Directions { get; } = [SyncDirection.TwoWay, SyncDirection.DownloadOnly, SyncDirection.UploadOnly];

    public bool TwoWay
    {
        get => Direction == SyncDirection.TwoWay;
        set => SetDirection(value, SyncDirection.TwoWay);
    }

    public bool DownloadOnly
    {
        get => Direction == SyncDirection.DownloadOnly;
        set => SetDirection(value, SyncDirection.DownloadOnly);
    }

    public bool UploadOnly
    {
        get => Direction == SyncDirection.UploadOnly;
        set => SetDirection(value, SyncDirection.UploadOnly);
    }

    /// <summary>Lädt frühere Verbindungen; gibt es welche, ist „frühere Verbindung“ vorgewählt.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var connections = await _setup.GetConnectionsAsync(ct);
        Connections.Clear();
        foreach (var connection in connections)
        {
            Connections.Add(connection);
        }

        UseExisting = Connections.Count > 0;
        SelectedConnection = Connections.FirstOrDefault();
        OnPropertyChanged(nameof(HasConnections));
    }

    partial void OnLocalFolderChanged(string? value) => OnPropertyChanged(nameof(LocalFolderText));

    partial void OnDirectionChanged(SyncDirection value)
    {
        OnPropertyChanged(nameof(TwoWay));
        OnPropertyChanged(nameof(DownloadOnly));
        OnPropertyChanged(nameof(UploadOnly));
    }

    private void SetDirection(bool selected, SyncDirection direction)
    {
        if (selected)
        {
            Direction = direction;
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        Error = null;
        switch (Step)
        {
            case WizardStep.Connection:
                await ConnectAsync(trustedFingerprint: null);
                break;
            case WizardStep.Folders:
                if (_remotePath is null || (LocalFolder ??= await DefaultLocalFolderAsync()) is null)
                {
                    Error = Texts.ErrorChooseFolders;
                    return;
                }

                Step = WizardStep.Options;
                break;
            default:
                await CreateAsync();
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Error = null;
        Step = Step == WizardStep.Options ? WizardStep.Folders : WizardStep.Connection;
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(false);

    /// <summary>Der Nutzer hat den Fingerabdruck geprüft und vertraut dem Zertifikat.</summary>
    [RelayCommand]
    private Task TrustCertificateAsync() => ConnectAsync(CertificateFingerprint);

    [RelayCommand]
    private void RejectCertificate() => CertificateFingerprint = null;

    [RelayCommand]
    private async Task ChooseServerFolderAsync()
    {
        if (_connectionId is null)
        {
            return;
        }

        var picker = new ServerFolderPickerViewModel(_setup, _connectionId, Texts);
        await picker.LoadAsync(CancellationToken.None);
        if (await _ui.PickServerFolderAsync(picker) is { } chosen)
        {
            _spaceId = chosen.SpaceId;
            _remotePath = chosen.Path;
            ServerFolderText = chosen.DisplayPath;
        }
    }

    [RelayCommand]
    private async Task ChooseLocalFolderAsync()
    {
        if (await _ui.PickLocalFolderAsync(LocalFolder ?? _suggestedLocalFolder) is not { } path)
        {
            return;
        }

        var violations = await _setup.CheckLocalFolderAsync(path, onDemand: false, CancellationToken.None);
        if (violations.Count > 0)
        {
            await _ui.ShowMessageAsync(Texts.ViolationText(violations[0]));
            return;
        }

        LocalFolder = path;
    }

    /// <summary>„SyncDrive“ im Benutzerordner; gehört er schon zu einer Aufgabe, „SyncDrive 2“, „SyncDrive 3“ … Angelegt wird er mit der Aufgabe.</summary>
    private async Task<string?> DefaultLocalFolderAsync()
    {
        for (var n = 1; n <= 9; n++)
        {
            var candidate = n == 1 ? _suggestedLocalFolder : $"{_suggestedLocalFolder} {n}";
            if ((await _setup.CheckLocalFolderAsync(candidate, onDemand: false, CancellationToken.None)).Count == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task ConnectAsync(string? trustedFingerprint)
    {
        if (UseExisting && SelectedConnection is { } existing)
        {
            _connectionId = existing.Id;
            Step = WizardStep.Folders;
            return;
        }

        if (Address.Trim().Length == 0)
        {
            Error = Texts.ErrorAddress;
            return;
        }

        IsBusy = true;
        try
        {
            var outcome = await _setup.SignInAsync(new SignInRequest(Address, UserName, Password, trustedFingerprint), CancellationToken.None);
            CertificateFingerprint = null;
            if (outcome.Succeeded)
            {
                _connectionId = outcome.ConnectionId;
                Password = string.Empty;
                Step = WizardStep.Folders;
                return;
            }

            switch (outcome.ErrorCode)
            {
                case SetupErrors.UntrustedCertificate:
                    CertificateFingerprint = outcome.CertificateSha256;
                    break;
                case SetupErrors.TooManyAttempts:
                    Error = Texts.ErrorTooManyAttempts;
                    break;
                case SetupErrors.SignInFailed:
                    Error = Texts.ErrorSignIn;
                    break;
                case SetupErrors.InvalidAddress:
                    Error = Texts.ErrorAddress;
                    break;
                default:
                    Error = Texts.ErrorUnreachable;
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateAsync()
    {
        var extensions = ExcludedExtensions
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('*', '.'))
            .Where(e => e.Length > 0)
            .ToList();
        var filters = new TaskFilterSettings(MaxFileSizeBytes: (long)(MaxFileSizeMb * 1024 * 1024), ExcludedExtensions: extensions);
        IsBusy = true;
        try
        {
            var outcome = await _setup.CreateTaskAsync(
                new NewTaskRequest(
                    _connectionId!, _spaceId!, _remotePath!, LocalFolder!, Direction, KeepLocallyDeleted && IsUploadOnly, OnDemand && OnDemandAvailable && !IsUploadOnly,
                    AdvancedConsistencyCheck: true, filters), // immer an: Inhalte werden gehasht statt Größe und Datum zu vertrauen
                CancellationToken.None);
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
}

/// <summary>Ordnerbaum des Servers (Ordnerauswahl): oben die Spaces, darunter Ebene für Ebene nachgeladene Ordner.</summary>
public sealed partial class ServerFolderPickerViewModel(IClientSetup setup, string connectionId, Texts texts) : ObservableObject
{
    [ObservableProperty]
    private FolderNodeViewModel? _selected;

    public Texts Texts { get; } = texts;

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    public async Task LoadAsync(CancellationToken ct)
    {
        Roots.Clear();
        foreach (var space in await setup.BrowseAsync(connectionId, null, string.Empty, ct))
        {
            Roots.Add(new FolderNodeViewModel(setup, connectionId, space.SpaceId, string.Empty, space.Name, space.Name));
        }
    }

    /// <summary>Ergebnis für den Assistenten: Space, Pfad darin und Anzeige „Space/Pfad“.</summary>
    public (string SpaceId, string Path, string DisplayPath)? Result =>
        Selected is { } node ? (node.SpaceId, node.Path, node.DisplayPath) : null;
}

public sealed partial class FolderNodeViewModel : ObservableObject
{
    private readonly IClientSetup _setup;
    private readonly string _connectionId;
    private bool _loaded;

    [ObservableProperty]
    private bool _isExpanded;

    public FolderNodeViewModel(IClientSetup setup, string connectionId, string spaceId, string path, string name, string displayPath)
    {
        _setup = setup;
        _connectionId = connectionId;
        SpaceId = spaceId;
        Path = path;
        Name = name;
        DisplayPath = displayPath;
        Children.Add(Placeholder); // Dreieck zum Aufklappen, Inhalt wird erst dann geladen
    }

    private FolderNodeViewModel(string name)
    {
        _setup = null!;
        _connectionId = string.Empty;
        SpaceId = string.Empty;
        Path = string.Empty;
        Name = name;
        DisplayPath = name;
        _loaded = true;
    }

    private static FolderNodeViewModel Placeholder => new("…");

    public string SpaceId { get; }

    public string Path { get; }

    public string Name { get; }

    public string DisplayPath { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            _ = LoadChildrenAsync();
        }
    }

    public async Task LoadChildrenAsync()
    {
        _loaded = true;
        var folders = await _setup.BrowseAsync(_connectionId, SpaceId, Path, CancellationToken.None);
        Children.Clear();
        foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Children.Add(new FolderNodeViewModel(_setup, _connectionId, SpaceId, folder.Path, folder.Name, $"{DisplayPath}/{folder.Name}"));
        }
    }

    public override string ToString() => RelativePath.TryParse(Path, out var p) && !p.IsRoot ? p.Value : Name;
}
