using System.Globalization;
using SyncDrive.Client.App.Tasks;

namespace SyncDrive.Client.Ui;

/// <summary>
/// Alle Texte der Oberfläche. Englisch ist die Grundlage,
/// Deutsch gleichwertig; gewählt wird nach Einstellung „Language“ oder Systemsprache.
/// </summary>
public sealed record Texts
{
    public required string AppName { get; init; }

    // Gesamtzustand (Tray, Panel-Kopf)
    public required string StateNormal { get; init; }

    public required string StateUpToDate { get; init; }

    public required string StateSyncing { get; init; }

    public required string StatePaused { get; init; }

    public required string StateError { get; init; }

    public required string StateDisconnected { get; init; }

    public required string DaemonNotRunning { get; init; }

    // Tray-Menü
    public required string MenuPause { get; init; }

    public required string MenuResume { get; init; }

    public required string MenuSettings { get; init; }

    public required string MenuAbout { get; init; }

    public required string MenuTroubleshoot { get; init; }

    public required string MenuExit { get; init; }

    // Panel
    public required string TabRecentlyChanged { get; init; }

    public required string TabNotifications { get; init; }

    public required string EmptyRecent { get; init; }

    public required string EmptyNotifications { get; init; }

    public required string Uploaded { get; init; }

    public required string Downloaded { get; init; }

    public required string Deleted { get; init; }

    public required string Conflict { get; init; }

    public required string Moved { get; init; }

    public required string Unsynced { get; init; }

    public required string JustNow { get; init; }

    /// <summary>Platzhalter {0} = Anzahl; Einheit mit Mehrzahlklammer („5 hour(s) ago“).</summary>
    public required string MinutesAgo { get; init; }

    public required string HoursAgo { get; init; }

    public required string DaysAgo { get; init; }

    // Hauptfenster
    public required string NavSyncTasks { get; init; }

    public required string NavBackupTask { get; init; }

    public required string NavLogs { get; init; }

    public required string NavNotifications { get; init; }

    public required string NavGlobalSettings { get; init; }

    public required string Create { get; init; }

    public required string Delete { get; init; }

    public required string EditConnection { get; init; }

    public required string SyncRules { get; init; }

    public required string TwoWay { get; init; }

    public required string DownloadOnly { get; init; }

    public required string UploadOnly { get; init; }

    public required string EmptySyncTasks { get; init; }

    public required string EmptyBackup { get; init; }

    public required string CreateBackupTask { get; init; }

    public required string BackupPlanned { get; init; }

    public required string ConfirmDeleteTask { get; init; }

    public required string TaskConnecting { get; init; }

    public required string TaskDisabled { get; init; }

    public required string TaskDone { get; init; }

    public required string TaskError { get; init; }

    public required string TaskPaused { get; init; }

    public required string TaskSyncing { get; init; }

    // Logs
    public required string AllTasks { get; init; }

    public required string AllEvents { get; init; }

    public required string Search { get; init; }

    public required string ColumnName { get; init; }

    public required string ColumnKind { get; init; }

    public required string ColumnTime { get; init; }

    public required string ColumnEvent { get; init; }

    public required string File { get; init; }

    public required string Folder { get; init; }

    public required string AllNotifications { get; init; }

    public required string ColumnNotification { get; init; }

    public required string ColumnServer { get; init; }

    // Einstellungen
    public required string TabGeneral { get; init; }

    public required string TabDisplay { get; init; }

    public required string TabProxy { get; init; }

    public required string TabUsage { get; init; }

    public required string StartWithWindows { get; init; }

    public required string OnRestartHeading { get; init; }

    public required string OnRestartRefetch { get; init; }

    public required string OnRestartPropagate { get; init; }

    public required string ConflictHeading { get; init; }

    public required string ConflictNewer { get; init; }

    public required string ConflictServer { get; init; }

    public required string RenameDiscarded { get; init; }

    public required string DesktopShortcutFolder { get; init; }

    public required string Language { get; init; }

    public required string LanguageSystem { get; init; }

    public required string ShowDesktopNotifications { get; init; }

    public required string ShowTips { get; init; }

    public required string TrayIconStyle { get; init; }

    public required string MinimalTrayIcon { get; init; }

    public required string LocalFolderDisplay { get; init; }

    public required string ShowContextMenu { get; init; }

    public required string ShowOverlays { get; init; }

    public required string ShowOverlaysForUnsynced { get; init; }

    public required string OverlayNote { get; init; }

    public required string ProxyNone { get; init; }

    public required string ProxyAuto { get; init; }

    public required string ProxyManual { get; init; }

    public required string ProxyHost { get; init; }

    public required string ProxyPort { get; init; }

    public required string ProxyAuthentication { get; init; }

    public required string UserName { get; init; }

    public required string Password { get; init; }

    public required string UsageExplanation { get; init; }

    // Assistent
    public required string WizardTitle { get; init; }

    public required string WizardConnectionHeading { get; init; }

    public required string WizardExistingConnection { get; init; }

    public required string WizardNewConnection { get; init; }

    public required string ServerAddress { get; init; }

    public required string ServerAddressHint { get; init; }

    public required string Checking { get; init; }

    public required string WizardFoldersHeading { get; init; }

    public required string WizardFoldersQuestion { get; init; }

    public required string ServerSide { get; init; }

    public required string YourComputer { get; init; }

    public required string NotYetSelected { get; init; }

    public required string DefaultLocalFolder { get; init; }

    public required string Select { get; init; }

    public required string WizardOptionsHeading { get; init; }

    public required string SyncMode { get; init; }

    public required string KeepLocallyDeleted { get; init; }

    public required string FileFilter { get; init; }

    public required string MaxFileSize { get; init; }

    public required string ExcludedExtensions { get; init; }

    public required string OnDemandOption { get; init; }

    public required string OnDemandHint { get; init; }

    public required string Back { get; init; }

    public required string Next { get; init; }

    public required string Done { get; init; }

    public required string Cancel { get; init; }

    public required string Ok { get; init; }

    public required string ChooseServerFolder { get; init; }

    public required string CertificateQuestion { get; init; }

    public required string Trust { get; init; }

    public required string ErrorUnreachable { get; init; }

    public required string ErrorSignIn { get; init; }
    public required string ErrorTooManyAttempts { get; init; }

    public required string ErrorAddress { get; init; }

    public required string ErrorChooseFolders { get; init; }

    public required string AboutText { get; init; }

    public required string NotificationShared { get; init; }

    public required string Save { get; init; }

    public required string SavePassword { get; init; }

    /// <summary>{0} = Größe („1.4 GB“).</summary>
    public required string UsedFormat { get; init; }

    public required string TabFolder { get; init; }

    public required string FolderRulesHint { get; init; }

    public required string ExcludedNamesLabel { get; init; }

    public required string ConnectionUpdated { get; init; }

    public required string ShellVersions { get; init; }

    public required string ShellGetLink { get; init; }

    public required string ShellKeep { get; init; }

    public required string ShellFreeUp { get; init; }

    public required string ShellStopSync { get; init; }

    public required string ShellResumeSync { get; init; }

    public required string VersionsTitle { get; init; }

    public required string ColumnSize { get; init; }

    public required string ColumnBy { get; init; }

    public required string SaveAs { get; init; }

    public required string Restore { get; init; }

    public required string CurrentVersion { get; init; }

    public required string NoVersions { get; init; }

    public required string ConfirmRestore { get; init; }

    public required string VersionSaved { get; init; }

    public required string VersionRestored { get; init; }

    public required string NotAllowed { get; init; }

    public required string FolderExcluded { get; init; }

    public required string FolderIncluded { get; init; }

    public required string NotInSyncFolder { get; init; }

    public required string NotPossibleForRoot { get; init; }

    public required string NotOnDemand { get; init; }

    public required string LinkCopied { get; init; }

    public required string LinkNeedsWeb { get; init; }

    public required string ActionFailed { get; init; }

    public string ViolationText(TaskRuleViolation violation) => violation switch
    {
        TaskRuleViolation.TooManyTasks => TooManyTasks,
        TaskRuleViolation.LocalFolderOverlapsTask => FolderOverlaps,
        TaskRuleViolation.LocalFolderInOtherCloud => FolderInOtherCloud,
        TaskRuleViolation.ReadOnlyRequiresDownloadOnly => ReadOnlyDownloadOnly,
        TaskRuleViolation.OnDemandRequiresNtfs => OnDemandNtfs,
        TaskRuleViolation.OnDemandNotInDriveOrHomeRoot => OnDemandRoot,
        _ => violation.ToString(),
    };

    public required string TooManyTasks { get; init; }

    public required string FolderOverlaps { get; init; }

    public required string FolderInOtherCloud { get; init; }

    public required string ReadOnlyDownloadOnly { get; init; }

    public required string OnDemandNtfs { get; init; }

    public required string OnDemandRoot { get; init; }

    public static Texts For(string language) => language switch
    {
        "de" => German,
        "en" => English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? German : English,
    };

    public static Texts English { get; } = new()
    {
        AppName = "SyncDrive",
        StateNormal = "No sync task",
        StateUpToDate = "Up-to-date",
        StateSyncing = "Syncing",
        StatePaused = "Paused",
        StateError = "Some items could not be synced",
        StateDisconnected = "Disconnected",
        DaemonNotRunning = "The sync service is not running",
        MenuPause = "Pause",
        MenuResume = "Resume",
        MenuSettings = "Settings",
        MenuAbout = "About",
        MenuTroubleshoot = "Troubleshoot",
        MenuExit = "Exit",
        TabRecentlyChanged = "Recently Changed",
        TabNotifications = "Notifications",
        EmptyRecent = "Nothing has changed yet",
        EmptyNotifications = "No notifications",
        Uploaded = "Uploaded",
        Downloaded = "Downloaded",
        Deleted = "Deleted",
        Conflict = "Conflict copy",
        Moved = "Moved",
        Unsynced = "Unsynced",
        JustNow = "just now",
        MinutesAgo = "{0} minute(s) ago",
        HoursAgo = "{0} hour(s) ago",
        DaysAgo = "{0} day(s) ago",
        NavSyncTasks = "Sync Tasks",
        NavBackupTask = "Backup Task",
        NavLogs = "Logs",
        NavNotifications = "Notifications",
        NavGlobalSettings = "Global Settings",
        Create = "Create",
        Delete = "Delete",
        EditConnection = "Edit Connection",
        SyncRules = "Sync Rules",
        TwoWay = "Two-way sync",
        DownloadOnly = "Download only",
        UploadOnly = "Upload only",
        EmptySyncTasks = "There is no sync task yet. Create one to keep a folder on this computer in sync with the server.",
        EmptyBackup = "Back up folders of this computer to the server, with schedule and versions.",
        CreateBackupTask = "Create Backup Task",
        BackupPlanned = "Backup tasks follow in a later version.",
        ConfirmDeleteTask = "Remove this sync task? Files on this computer and on the server stay where they are.",
        TaskConnecting = "Connecting",
        TaskDisabled = "Stopped",
        TaskDone = "Up-to-date",
        TaskError = "Some items could not be synced",
        TaskPaused = "Paused",
        TaskSyncing = "Syncing",
        AllTasks = "All tasks",
        AllEvents = "All events",
        Search = "Search",
        ColumnName = "Name",
        ColumnKind = "File/Folder",
        ColumnTime = "Date & Time",
        ColumnEvent = "Event",
        File = "File",
        Folder = "Folder",
        AllNotifications = "All notifications",
        ColumnNotification = "Notifications",
        ColumnServer = "Server Name",
        TabGeneral = "General",
        TabDisplay = "Display",
        TabProxy = "Proxy",
        TabUsage = "Usage",
        StartWithWindows = "Start SyncDrive when I sign in",
        OnRestartHeading = "When the app restarts or the connection resumes",
        OnRestartRefetch = "Download files again that were deleted on this computer",
        OnRestartPropagate = "Delete them on the server as well",
        ConflictHeading = "When both sides changed a file",
        ConflictNewer = "Keep the most recent change",
        ConflictServer = "Keep the version on the server",
        RenameDiscarded = "Keep the other version under a new name",
        DesktopShortcutFolder = "Folder opened by the desktop shortcut",
        Language = "Language",
        LanguageSystem = "Same as system",
        ShowDesktopNotifications = "Show desktop notifications for file events",
        ShowTips = "Show tips at next start",
        TrayIconStyle = "Tray icon style",
        MinimalTrayIcon = "Use a minimal tray icon",
        LocalFolderDisplay = "In local folders",
        ShowContextMenu = "Show the context menu for files and folders",
        ShowOverlays = "Show status icons on files and folders",
        ShowOverlaysForUnsynced = "Also for files that are not synced",
        OverlayNote = "Note: on-demand tasks always show their own status icons.",
        ProxyNone = "No proxy",
        ProxyAuto = "Detect automatically",
        ProxyManual = "Manual",
        ProxyHost = "Server",
        ProxyPort = "Port",
        ProxyAuthentication = "Proxy requires sign-in",
        UserName = "User name",
        Password = "Password",
        UsageExplanation = "Space used by each synced folder on the server.",
        WizardTitle = "Create a new sync task",
        WizardConnectionHeading = "Connect to your server",
        WizardExistingConnection = "Choose a previous connection",
        WizardNewConnection = "Connect to a new server",
        ServerAddress = "Server",
        ServerAddressHint = "Address or host name, e.g. nas.local:8443",
        Checking = "Checking user name and password…",
        WizardFoldersHeading = "Choose folders",
        WizardFoldersQuestion = "Choose which folder on the server stays in sync with which folder on this computer.",
        ServerSide = "Server",
        YourComputer = "Your computer",
        NotYetSelected = "Not yet selected",
        DefaultLocalFolder = "{0} (created automatically)",
        Select = "Select…",
        WizardOptionsHeading = "Options",
        SyncMode = "Sync mode",
        KeepLocallyDeleted = "Keep files on the server that were deleted on this computer",
        FileFilter = "File filter",
        MaxFileSize = "Maximum file size in MB (0 = no limit)",
        ExcludedExtensions = "Skip file extensions (e.g. iso, tmp)",
        OnDemandOption = "Enable On-demand Sync to save local storage space",
        OnDemandHint = "Files stay on the server and are downloaded when you open them.",
        Back = "Back",
        Next = "Next",
        Done = "Done",
        Cancel = "Cancel",
        Ok = "OK",
        ChooseServerFolder = "Choose a folder on the server",
        CertificateQuestion = "The server presents a certificate this computer does not know. Only continue if this fingerprint matches the one on your server:",
        Trust = "Trust and continue",
        ErrorUnreachable = "The server cannot be reached. Check the address, the network and the proxy settings.",
        ErrorSignIn = "User name or password is wrong.",
        ErrorTooManyAttempts = "Too many failed attempts. Please try again in a few minutes.",
        ErrorAddress = "Please enter a server address.",
        ErrorChooseFolders = "Please choose a folder on the server and one on this computer.",
        AboutText = "SyncDrive keeps folders on this computer in sync with your server.",
        NotificationShared = "shared",
        Save = "Save",
        SavePassword = "Save password",
        UsedFormat = "{0} used",
        TabFolder = "Folder",
        FolderRulesHint = "Only checked folders are synced to this computer.",
        ExcludedNamesLabel = "Skip file names (e.g. Thumbs.db)",
        ConnectionUpdated = "Signed in again.",
        ShellVersions = "Browse previous versions",
        ShellGetLink = "Get link",
        ShellKeep = "Always keep on this device",
        ShellFreeUp = "Free up space",
        ShellStopSync = "Stop syncing this folder",
        ShellResumeSync = "Sync this folder again",
        VersionsTitle = "Previous versions",
        ColumnSize = "Size",
        ColumnBy = "Modified by",
        SaveAs = "Save as…",
        Restore = "Restore",
        CurrentVersion = "current",
        NoVersions = "There are no previous versions of this file.",
        ConfirmRestore = "Make this version the current one? The newer version stays in the version list.",
        VersionSaved = "The version was saved.",
        VersionRestored = "The version is the current one again and will be synced.",
        NotAllowed = "You are not allowed to do this.",
        FolderExcluded = "This folder is no longer synced. Its files stay on this computer.",
        FolderIncluded = "This folder is synced again.",
        NotInSyncFolder = "This item is not in a SyncDrive folder.",
        NotPossibleForRoot = "The sync folder itself can only be removed as a whole task.",
        NotOnDemand = "Only available in sync tasks with files on demand.",
        LinkCopied = "The link was copied to the clipboard:\n{0}",
        LinkNeedsWeb = "The server requires a password or an expiry date for links. Please create the link in the web interface (Share → Public link).",
        ActionFailed = "That did not work:",
        TooManyTasks = "You already have the maximum number of sync tasks.",
        FolderOverlaps = "This folder cannot be synced because it is, contains or lies in a folder of another sync task. Please choose a different folder.",
        FolderInOtherCloud = "This folder is synced by another cloud service. Please choose a different folder.",
        ReadOnlyDownloadOnly = "You may only read this server folder, so only 'Download only' is possible.",
        OnDemandNtfs = "On-demand sync needs an NTFS drive.",
        OnDemandRoot = "On-demand sync cannot use the root of a drive or of your user folder.",
    };

    public static Texts German { get; } = English with
    {
        StateNormal = "Keine Sync-Aufgabe",
        StateUpToDate = "Aktuell",
        StateSyncing = "Wird synchronisiert",
        StatePaused = "Angehalten",
        StateError = "Einige Elemente wurden nicht synchronisiert",
        StateDisconnected = "Keine Verbindung",
        DaemonNotRunning = "Der Sync-Dienst läuft nicht",
        MenuPause = "Anhalten",
        MenuResume = "Fortsetzen",
        MenuSettings = "Einstellungen",
        MenuAbout = "Info",
        MenuTroubleshoot = "Fehlersuche",
        MenuExit = "Beenden",
        TabRecentlyChanged = "Zuletzt geändert",
        TabNotifications = "Benachrichtigungen",
        EmptyRecent = "Noch keine Änderungen",
        EmptyNotifications = "Keine Benachrichtigungen",
        Uploaded = "Hochgeladen",
        Downloaded = "Heruntergeladen",
        Deleted = "Gelöscht",
        Conflict = "Konfliktkopie",
        Moved = "Verschoben",
        Unsynced = "Nicht synchronisiert",
        JustNow = "gerade eben",
        MinutesAgo = "vor {0} Minute(n)",
        HoursAgo = "vor {0} Stunde(n)",
        DaysAgo = "vor {0} Tag(en)",
        NavSyncTasks = "Sync-Aufgaben",
        NavBackupTask = "Sicherung",
        NavLogs = "Protokoll",
        NavNotifications = "Benachrichtigungen",
        NavGlobalSettings = "Einstellungen",
        Create = "Erstellen",
        Delete = "Löschen",
        EditConnection = "Verbindung bearbeiten",
        SyncRules = "Sync-Regeln",
        TwoWay = "Beidseitig",
        DownloadOnly = "Nur herunterladen",
        UploadOnly = "Nur hochladen",
        EmptySyncTasks = "Es gibt noch keine Sync-Aufgabe. Legen Sie eine an, um einen Ordner dieses Computers mit dem Server abzugleichen.",
        EmptyBackup = "Ordner dieses Computers auf dem Server sichern, mit Zeitplan und Versionen.",
        CreateBackupTask = "Sicherung erstellen",
        BackupPlanned = "Sicherungsaufgaben folgen in einer späteren Version.",
        ConfirmDeleteTask = "Diese Sync-Aufgabe entfernen? Dateien auf diesem Computer und auf dem Server bleiben erhalten.",
        TaskConnecting = "Verbinde",
        TaskDisabled = "Gestoppt",
        TaskDone = "Aktuell",
        TaskError = "Einige Elemente wurden nicht synchronisiert",
        TaskPaused = "Angehalten",
        TaskSyncing = "Wird synchronisiert",
        AllTasks = "Alle Aufgaben",
        AllEvents = "Alle Ereignisse",
        Search = "Suchen",
        ColumnKind = "Datei/Ordner",
        ColumnTime = "Datum und Uhrzeit",
        ColumnEvent = "Ereignis",
        File = "Datei",
        Folder = "Ordner",
        AllNotifications = "Alle Benachrichtigungen",
        ColumnNotification = "Benachrichtigung",
        ColumnServer = "Server",
        TabGeneral = "Allgemein",
        TabDisplay = "Anzeige",
        TabUsage = "Speicher",
        StartWithWindows = "SyncDrive beim Anmelden starten",
        OnRestartHeading = "Wenn die App neu startet oder die Verbindung wiederkommt",
        OnRestartRefetch = "Auf diesem Computer gelöschte Dateien erneut herunterladen",
        OnRestartPropagate = "Sie auch auf dem Server löschen",
        ConflictHeading = "Wenn beide Seiten eine Datei geändert haben",
        ConflictNewer = "Neueste Änderung behalten",
        ConflictServer = "Version auf dem Server behalten",
        RenameDiscarded = "Die andere Version unter neuem Namen behalten",
        DesktopShortcutFolder = "Ordner der Desktopverknüpfung",
        Language = "Sprache",
        LanguageSystem = "Wie das System",
        ShowDesktopNotifications = "Desktop-Benachrichtigungen für Dateiereignisse anzeigen",
        ShowTips = "Tipps beim nächsten Start anzeigen",
        TrayIconStyle = "Tray-Symbol",
        MinimalTrayIcon = "Schlichtes Tray-Symbol verwenden",
        LocalFolderDisplay = "In lokalen Ordnern",
        ShowContextMenu = "Kontextmenü für Dateien und Ordner anzeigen",
        ShowOverlays = "Statussymbole an Dateien und Ordnern anzeigen",
        ShowOverlaysForUnsynced = "Auch für nicht synchronisierte Dateien",
        OverlayNote = "Hinweis: On-Demand-Aufgaben zeigen ihre Statussymbole immer.",
        ProxyNone = "Kein Proxy",
        ProxyAuto = "Automatisch erkennen",
        ProxyManual = "Manuell",
        ProxyAuthentication = "Proxy verlangt Anmeldung",
        UserName = "Benutzername",
        Password = "Passwort",
        UsageExplanation = "Belegter Speicher je synchronisiertem Ordner auf dem Server.",
        WizardTitle = "Neue Sync-Aufgabe erstellen",
        WizardConnectionHeading = "Mit dem Server verbinden",
        WizardExistingConnection = "Frühere Verbindung wählen",
        WizardNewConnection = "Mit neuem Server verbinden",
        ServerAddressHint = "Adresse oder Hostname, z. B. nas.local:8443",
        Checking = "Benutzername und Passwort werden geprüft …",
        WizardFoldersHeading = "Ordner wählen",
        WizardFoldersQuestion = "Wählen Sie, welcher Ordner auf dem Server mit welchem Ordner dieses Computers abgeglichen wird.",
        YourComputer = "Ihr Computer",
        NotYetSelected = "Noch nicht gewählt",
        DefaultLocalFolder = "{0} (wird automatisch angelegt)",
        Select = "Wählen …",
        WizardOptionsHeading = "Optionen",
        SyncMode = "Sync-Modus",
        KeepLocallyDeleted = "Auf diesem Computer gelöschte Dateien auf dem Server behalten",
        FileFilter = "Dateifilter",
        MaxFileSize = "Höchste Dateigröße in MB (0 = unbegrenzt)",
        ExcludedExtensions = "Dateiendungen auslassen (z. B. iso, tmp)",
        OnDemandOption = "On-Demand-Sync aktivieren, um lokalen Speicherplatz zu sparen",
        OnDemandHint = "Dateien bleiben auf dem Server und werden beim Öffnen heruntergeladen.",
        Back = "Zurück",
        Next = "Weiter",
        Done = "Fertig",
        Cancel = "Abbrechen",
        ChooseServerFolder = "Ordner auf dem Server wählen",
        CertificateQuestion = "Der Server zeigt ein Zertifikat, das dieser Computer nicht kennt. Fahren Sie nur fort, wenn dieser Fingerabdruck zu Ihrem Server passt:",
        Trust = "Vertrauen und fortfahren",
        ErrorUnreachable = "Der Server ist nicht erreichbar. Prüfen Sie Adresse, Netzwerk und Proxy-Einstellungen.",
        ErrorSignIn = "Benutzername oder Passwort ist falsch.",
        ErrorTooManyAttempts = "Zu viele Fehlversuche. Bitte in ein paar Minuten erneut versuchen.",
        ErrorAddress = "Bitte eine Serveradresse eingeben.",
        ErrorChooseFolders = "Bitte einen Ordner auf dem Server und einen auf diesem Computer wählen.",
        AboutText = "SyncDrive hält Ordner dieses Computers mit Ihrem Server abgeglichen.",
        NotificationShared = "hat geteilt:",
        Save = "Speichern",
        SavePassword = "Passwort speichern",
        UsedFormat = "{0} belegt",
        TabFolder = "Ordner",
        FolderRulesHint = "Nur angehakte Ordner werden mit diesem Computer synchronisiert.",
        ExcludedNamesLabel = "Dateinamen auslassen (z. B. Thumbs.db)",
        ConnectionUpdated = "Erneut angemeldet.",
        ShellVersions = "Frühere Versionen durchsuchen",
        ShellGetLink = "Link erhalten",
        ShellKeep = "Immer auf diesem Gerät behalten",
        ShellFreeUp = "Speicherplatz freigeben",
        ShellStopSync = "Diesen Ordner nicht mehr synchronisieren",
        ShellResumeSync = "Diesen Ordner wieder synchronisieren",
        VersionsTitle = "Frühere Versionen",
        ColumnSize = "Größe",
        ColumnBy = "Geändert von",
        SaveAs = "Speichern unter …",
        Restore = "Wiederherstellen",
        CurrentVersion = "aktuell",
        NoVersions = "Von dieser Datei gibt es keine früheren Versionen.",
        ConfirmRestore = "Diese Version wieder zur aktuellen machen? Die neuere bleibt in der Versionsliste.",
        VersionSaved = "Die Version wurde gespeichert.",
        VersionRestored = "Die Version ist wieder die aktuelle und wird synchronisiert.",
        NotAllowed = "Dazu fehlt Ihnen die Berechtigung.",
        FolderExcluded = "Dieser Ordner wird nicht mehr synchronisiert. Seine Dateien bleiben auf diesem Computer.",
        FolderIncluded = "Dieser Ordner wird wieder synchronisiert.",
        NotInSyncFolder = "Dieses Element liegt in keinem SyncDrive-Ordner.",
        NotPossibleForRoot = "Der Sync-Ordner selbst lässt sich nur als ganze Aufgabe entfernen.",
        NotOnDemand = "Nur in Sync-Aufgaben mit Dateien auf Abruf möglich.",
        LinkCopied = "Der Link wurde in die Zwischenablage kopiert:\n{0}",
        LinkNeedsWeb = "Der Server verlangt für Links ein Passwort oder Ablaufdatum. Bitte den Link in der Weboberfläche anlegen (Freigeben → Öffentlicher Link).",
        ActionFailed = "Das hat nicht geklappt:",
        TooManyTasks = "Sie haben bereits die größtmögliche Zahl an Sync-Aufgaben.",
        FolderOverlaps = "Dieser Ordner kann nicht synchronisiert werden, weil er Ordner einer anderen Sync-Aufgabe ist, enthält oder darin liegt. Bitte wählen Sie einen anderen Ordner.",
        FolderInOtherCloud = "Diesen Ordner synchronisiert bereits ein anderer Cloud-Dienst. Bitte wählen Sie einen anderen Ordner.",
        ReadOnlyDownloadOnly = "Auf diesen Serverordner haben Sie nur Leserechte; möglich ist nur „Nur herunterladen“.",
        OnDemandNtfs = "On-Demand-Sync braucht ein NTFS-Laufwerk.",
        OnDemandRoot = "On-Demand-Sync geht nicht im Stamm eines Laufwerks oder Ihres Benutzerordners.",
    };
}
