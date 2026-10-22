using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>Texte der Weboberfläche (englisch und deutsch); Auswahl nach der Sprache des Browsers.</summary>
public sealed record WebTexts
{
    public required string AppName { get; init; }
    public required string SignInTitle { get; init; }
    public required string UserName { get; init; }
    public required string Password { get; init; }
    public required string SignIn { get; init; }
    public required string SignOut { get; init; }
    public required string SignInFailed { get; init; }
    public required string MyDrive { get; init; }
    public required string TeamFolders { get; init; }
    public required string Create { get; init; }
    public required string NewFolder { get; init; }
    public required string Upload { get; init; }
    public required string Download { get; init; }
    public required string Rename { get; init; }
    public required string Delete { get; init; }
    public required string Open { get; init; }
    public required string Cancel { get; init; }
    public required string Ok { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string ModifiedTime { get; init; }
    public required string Size { get; init; }
    public required string Loading { get; init; }
    public required string EmptyFolder { get; init; }
    public required string DropHere { get; init; }
    public required string ConfirmDelete { get; init; }
    public required string Uploading { get; init; }
    public required string NameTaken { get; init; }
    public required string InvalidName { get; init; }
    public required string ChangedMeanwhile { get; init; }
    public required string NoPermission { get; init; }
    public required string NotSignedIn { get; init; }
    public required string GenericError { get; init; }
    public required string SelectedCount { get; init; }
    public required string Recent { get; init; }
    public required string RecycleBin { get; init; }
    public required string Search { get; init; }
    public required string SearchIn { get; init; }
    public required string SearchAll { get; init; }
    public required string SearchResults { get; init; }
    public required string NoResults { get; init; }
    public required string NoRecent { get; init; }
    public required string EmptyTrash { get; init; }
    public required string Location { get; init; }
    public required string DeletedTime { get; init; }
    public required string Restore { get; init; }
    public required string Restored { get; init; }
    public required string OpenLocation { get; init; }
    public required string Versions { get; init; }
    public required string VersionsOf { get; init; }
    public required string Current { get; init; }
    public required string SavedBy { get; init; }
    public required string Close { get; init; }
    public required string VersionRestored { get; init; }
    public required string SignInTo { get; init; }
    public required string SignInButton { get; init; }
    public required string SignInTagline { get; init; }
    public required string LoginPortal { get; init; }
    public required string ServerName { get; init; }
    public required string LoginMessage { get; init; }
    public required string Background { get; init; }
    public required string ChooseImage { get; init; }
    public required string RemoveImage { get; init; }
    public required string ImageHint { get; init; }
    public required string ImageTooLarge { get; init; }
    public required string Save { get; init; }
    public required string Saved { get; init; }
    public required string Starred { get; init; }
    public required string AddStar { get; init; }
    public required string RemoveStar { get; init; }
    public required string NoStarred { get; init; }
    public required string Labels { get; init; }
    public required string MyLabels { get; init; }
    public required string NewLabel { get; init; }
    public required string Color { get; init; }
    public required string DeleteLabel { get; init; }
    public required string ConfirmDeleteLabel { get; init; }
    public required string NoLabeled { get; init; }
    public required string NoLabels { get; init; }
    public required string LabelExists { get; init; }
    public required string InvalidLabelName { get; init; }
    public required string Notifications { get; init; }
    public required string NoNotifications { get; init; }
    public required string MarkAllRead { get; init; }
    public required string NotifyShared { get; init; }
    public required string StorageUsed { get; init; }
    public required string StorageUsedOf { get; init; }
    public required string ClientList { get; init; }
    public required string DeviceName { get; init; }
    public required string ClientType { get; init; }
    public required string Version { get; init; }
    public required string LastConnection { get; init; }
    public required string Unlink { get; init; }
    public required string Refresh { get; init; }
    public required string ThisBrowser { get; init; }
    public required string ClientWeb { get; init; }
    public required string ClientDesktop { get; init; }
    public required string ConfirmUnlink { get; init; }
    public required string DeviceUnlinked { get; init; }
    public required string Categories { get; init; }
    public required string Share { get; init; }
    public required string ShareTitle { get; init; }
    public required string Permissions { get; init; }
    public required string PublicLink { get; init; }
    public required string PeopleWithAccess { get; init; }
    public required string NobodyYet { get; init; }
    public required string Invite { get; init; }
    public required string Remove { get; init; }
    public required string RolePreviewer { get; init; }
    public required string RoleViewer { get; init; }
    public required string RoleEditor { get; init; }
    public required string RoleManager { get; init; }
    public required string RoleOwner { get; init; }
    public required string OnlyFolders { get; init; }
    public required string CreateLink { get; init; }
    public required string LinkHint { get; init; }
    public required string PasswordProtect { get; init; }
    public required string PasswordKept { get; init; }
    public required string ExpiryDate { get; init; }
    public required string CopyLink { get; init; }
    public required string LinkCopied { get; init; }
    public required string RequestFiles { get; init; }
    public required string RequestTitle { get; init; }
    public required string CreateRequest { get; init; }
    public required string RequestHint { get; init; }
    public required string FileRequest { get; init; }
    public required string PasswordMissing { get; init; }
    public required string PasswordTooShort { get; init; }
    public required string AlreadyHasAccess { get; init; }
    public required string UnknownUser { get; init; }
    public required string NoSharePermission { get; init; }
    public required string SharedWithMe { get; init; }
    public required string SharedWithOthers { get; init; }
    public required string FileRequests { get; init; }
    public required string SharedBy { get; init; }
    public required string Access { get; init; }
    public required string Sharing { get; init; }
    public required string NothingSharedWithMe { get; init; }
    public required string NothingShared { get; init; }
    public required string NoFileRequests { get; init; }
    public required string Created { get; init; }
    public required string Expires { get; init; }
    public required string Status { get; init; }
    public required string Active { get; init; }
    public required string Expired { get; init; }
    public required string Never { get; init; }
    public required string ManageSharing { get; init; }
    public required string PeopleCount { get; init; }
    public required string LeaveShare { get; init; }
    public required string ConfirmLeave { get; init; }
    public required string LinkExpired { get; init; }
    public required string LinkNotFound { get; init; }
    public required string LinkExpiresOn { get; init; }
    public required string PasswordPrompt { get; init; }
    public required string WrongPassword { get; init; }
    public required string Unlock { get; init; }
    public required string DownloadAll { get; init; }
    public required string SharedByOwner { get; init; }
    public required string RequestPageHint { get; init; }
    public required string ChooseFiles { get; init; }
    public required string DropToUpload { get; init; }
    public required string UploadedAs { get; init; }
    public required string Administration { get; init; }
    public required string Users { get; init; }
    public required string NewUser { get; init; }
    public required string Administrator { get; init; }
    public required string DisabledUser { get; init; }
    public required string DisableUser { get; init; }
    public required string EnableUser { get; init; }
    public required string UserSettings { get; init; }
    public required string ResetPassword { get; init; }
    public required string NewPassword { get; init; }
    public required string QuotaGb { get; init; }
    public required string Unlimited { get; init; }
    public required string Storage { get; init; }
    public required string DevicesColumn { get; init; }
    public required string UsedOf { get; init; }
    public required string InvalidQuota { get; init; }
    public required string UserPasswordTooShort { get; init; }
    public required string UserCreated { get; init; }
    public required string CannotChangeSelf { get; init; }
    public required string PasswordReset { get; init; }
    public required string UserExists { get; init; }
    public required string QuotaExceeded { get; init; }
    public required string You { get; init; }
    public required string Groups { get; init; }
    public required string TeamFoldersTab { get; init; }
    public required string NewGroup { get; init; }
    public required string DeleteGroup { get; init; }
    public required string ConfirmDeleteGroup { get; init; }
    public required string MembersOf { get; init; }
    public required string NoGroups { get; init; }
    public required string NewTeamFolder { get; init; }
    public required string NoTeamFolders { get; init; }
    public required string Members { get; init; }
    public required string MembersSummary { get; init; }
    public required string NoMembers { get; init; }
    public required string KindUser { get; init; }
    public required string KindGroup { get; init; }
    public required string ChooseMember { get; init; }
    public required string NameTakenAdmin { get; init; }
    public required string TooManyAttempts { get; init; }
    public required string PoliciesTab { get; init; }
    public required string AllowPublicLinks { get; init; }
    public required string AllowFileRequests { get; init; }
    public required string RequirePassword { get; init; }
    public required string LimitDays { get; init; }
    public required string PoliciesHint { get; init; }
    public required string InvalidDays { get; init; }
    public required string ExpiryTooLate { get; init; }
    public required string LinksDisabled { get; init; }
    public required string PolicyEnforced { get; init; }

    public static WebTexts English { get; } = new()
    {
        AppName = "SyncDrive",
        SignInTitle = "Sign in to SyncDrive",
        UserName = "Username",
        Password = "Password",
        SignIn = "Sign in",
        SignOut = "Sign out",
        SignInFailed = "Wrong username or password.",
        MyDrive = "My Drive",
        TeamFolders = "Team Folder",
        Create = "Create",
        NewFolder = "Folder",
        Upload = "Upload",
        Download = "Download",
        Rename = "Rename",
        Delete = "Delete",
        Open = "Open",
        Cancel = "Cancel",
        Ok = "OK",
        Name = "Name",
        Title = "Title",
        ModifiedTime = "Modified time",
        Size = "Size",
        Loading = "Loading...",
        EmptyFolder = "Click Create or drop files here to add files or folders.",
        DropHere = "Drop files to upload",
        ConfirmDelete = "Move {0} item(s) to the Recycle Bin?",
        Uploading = "Uploading {0}",
        NameTaken = "An item with this name already exists.",
        InvalidName = "This name is not allowed.",
        ChangedMeanwhile = "The item was changed in the meantime. The list has been refreshed.",
        NoPermission = "You do not have permission for this action.",
        NotSignedIn = "Your session has ended. Please sign in again.",
        GenericError = "The action failed: {0}",
        SelectedCount = "{0} selected",
        Recent = "Recent",
        RecycleBin = "Recycle Bin",
        Search = "Search",
        SearchIn = "in {0}",
        SearchAll = "Search all locations",
        SearchResults = "Search results for “{0}”",
        NoResults = "No matching files or folders.",
        NoRecent = "No recently changed files.",
        EmptyTrash = "The Recycle Bin is empty.",
        Location = "Location",
        DeletedTime = "Deleted time",
        Restore = "Restore",
        Restored = "Restored {0} item(s) to their original location.",
        OpenLocation = "Open file location",
        Versions = "Versions",
        VersionsOf = "Versions of {0}",
        Current = "current",
        SavedBy = "Saved by",
        Close = "Close",
        VersionRestored = "The selected version is now the current one.",
        SignInTo = "Log in to {0}",
        SignInButton = "Log in",
        SignInTagline = "SyncDrive – your files on your own server",
        LoginPortal = "Login portal",
        ServerName = "Server name",
        LoginMessage = "Text below the name",
        Background = "Background image",
        ChooseImage = "Choose image…",
        RemoveImage = "Remove image",
        ImageHint = "JPEG, PNG or WebP, up to 5 MB.",
        ImageTooLarge = "The image is larger than 5 MB.",
        Save = "Save",
        Saved = "Saved.",
        Starred = "Starred",
        AddStar = "Add star",
        RemoveStar = "Remove star",
        NoStarred = "Click the star next to a file or folder to find it here.",
        Labels = "Labels",
        MyLabels = "My Labels",
        NewLabel = "New label",
        Color = "Color",
        DeleteLabel = "Delete label",
        ConfirmDeleteLabel = "Delete the label “{0}”? The files and folders stay where they are; only the label is removed.",
        NoLabeled = "No files or folders carry this label yet.",
        NoLabels = "No labels yet.",
        LabelExists = "A label with this name already exists.",
        InvalidLabelName = "Please enter a name with up to 64 characters.",
        Notifications = "Notification Center",
        NoNotifications = "No notifications.",
        MarkAllRead = "Mark all as read",
        NotifyShared = "{0} shared “{1}” with you",
        StorageUsed = "{0} used",
        StorageUsedOf = "{0} of {1} used",
        ClientList = "Client list",
        DeviceName = "Device name",
        ClientType = "Client type",
        Version = "Version",
        LastConnection = "Last connection",
        Unlink = "Unlink",
        Refresh = "Refresh",
        ThisBrowser = "this browser",
        ClientWeb = "Web browser",
        ClientDesktop = "Desktop client",
        ConfirmUnlink = "Unlink “{0}”? The device has to sign in again before it can sync.",
        DeviceUnlinked = "“{0}” was unlinked.",
        Categories = "Categories",
        Share = "Share",
        ShareTitle = "Share “{0}”",
        Permissions = "Permissions",
        PublicLink = "Public link",
        PeopleWithAccess = "Invitee list",
        NobodyYet = "No one has access yet.",
        Invite = "Add",
        Remove = "Remove",
        RolePreviewer = "Previewer",
        RoleViewer = "Viewer",
        RoleEditor = "Editor",
        RoleManager = "Manager",
        RoleOwner = "Owner",
        OnlyFolders = "Only folders can be shared with people. Use a public link for files.",
        CreateLink = "Create a public link",
        LinkHint = "Anyone with the link can view and download.",
        PasswordProtect = "Protect with a password",
        PasswordKept = "A password is set. Leave the field empty to keep it.",
        ExpiryDate = "Expiration date",
        CopyLink = "Copy link",
        LinkCopied = "Link copied: {0}",
        RequestFiles = "Request files",
        RequestTitle = "Request files for “{0}”",
        CreateRequest = "Create an upload link",
        RequestHint = "Anyone with the link can upload files to this folder but cannot see what is in it.",
        FileRequest = "file request",
        PasswordMissing = "Please enter a password.",
        PasswordTooShort = "The password needs at least 4 characters.",
        AlreadyHasAccess = "This user already has access.",
        UnknownUser = "There is no user with this name.",
        NoSharePermission = "You cannot share this item.",
        SharedWithMe = "Shared with me",
        SharedWithOthers = "Shared with others",
        FileRequests = "File Request",
        SharedBy = "Shared by",
        Access = "Access",
        Sharing = "Sharing",
        NothingSharedWithMe = "Nobody has shared a folder with you yet.",
        NothingShared = "You have not shared anything yet.",
        NoFileRequests = "No file requests. Right-click a folder and choose “Request files”.",
        Created = "Created",
        Expires = "Expires",
        Status = "Status",
        Active = "Active",
        Expired = "Expired",
        Never = "Never",
        ManageSharing = "Manage sharing…",
        PeopleCount = "{0} people",
        LeaveShare = "Leave",
        ConfirmLeave = "Leave “{0}”? You will no longer see this folder.",
        LinkExpired = "This link has expired.",
        LinkNotFound = "This link does not exist or is no longer shared.",
        LinkExpiresOn = "Available until {0}",
        PasswordPrompt = "This link is protected. Please enter the password.",
        WrongPassword = "Wrong password.",
        Unlock = "Open",
        DownloadAll = "Download all",
        SharedByOwner = "Shared by {0}",
        RequestPageHint = "{0} asks you to upload files to “{1}”.",
        ChooseFiles = "Choose files",
        DropToUpload = "Drop files here or choose them.",
        UploadedAs = "Uploaded: {0}",
        Administration = "Administration",
        Users = "Users",
        NewUser = "New user",
        Administrator = "Administrator",
        DisabledUser = "Disabled",
        DisableUser = "Disable",
        EnableUser = "Enable",
        UserSettings = "Settings",
        ResetPassword = "Reset password",
        NewPassword = "New password",
        QuotaGb = "Storage quota (GB)",
        Unlimited = "unlimited",
        Storage = "Storage",
        DevicesColumn = "Devices",
        UsedOf = "{0} of {1}",
        InvalidQuota = "The quota must be zero or more.",
        UserPasswordTooShort = "The password needs at least 10 characters.",
        UserCreated = "User “{0}” created.",
        CannotChangeSelf = "You cannot disable your own account or remove your own administrator rights.",
        PasswordReset = "New password set for “{0}”. All of their devices have to sign in again.",
        UserExists = "A user with this name already exists.",
        QuotaExceeded = "The storage quota is used up.",
        You = "you",
        Groups = "Groups",
        TeamFoldersTab = "Team folders",
        NewGroup = "New group",
        DeleteGroup = "Delete group",
        ConfirmDeleteGroup = "Delete the group “{0}”? Its members lose the access they had through this group.",
        MembersOf = "Members of “{0}”",
        NoGroups = "No groups yet.",
        NewTeamFolder = "New team folder",
        NoTeamFolders = "No team folders yet.",
        Members = "Members",
        MembersSummary = "{0} user(s), {1} group(s)",
        NoMembers = "No members yet.",
        KindUser = "User",
        KindGroup = "Group",
        ChooseMember = "Choose a user or group",
        NameTakenAdmin = "This name is already taken.",
        TooManyAttempts = "Too many failed attempts. Please try again in a few minutes.",
        PoliciesTab = "Policies",
        AllowPublicLinks = "Allow public links",
        AllowFileRequests = "Allow file requests",
        RequirePassword = "Require a password",
        LimitDays = "Limit validity to (days)",
        PoliciesHint = "Switching a kind of link off also blocks the links that were already sent. Password and validity rules apply to new or changed links.",
        InvalidDays = "Please enter between 1 and 3650 days.",
        ExpiryTooLate = "The administrator allows at most {0} days. Please choose an earlier date.",
        LinksDisabled = "Public links are switched off by the administrator.",
        PolicyEnforced = "set by the administrator",
    };

    public static WebTexts German { get; } = English with
    {
        SignInTitle = "Bei SyncDrive anmelden",
        UserName = "Benutzername",
        Password = "Passwort",
        SignIn = "Anmelden",
        SignOut = "Abmelden",
        SignInFailed = "Benutzername oder Passwort falsch.",
        MyDrive = "Meine Dateien",
        TeamFolders = "Team-Ordner",
        Create = "Erstellen",
        NewFolder = "Ordner",
        Upload = "Hochladen",
        Download = "Herunterladen",
        Rename = "Umbenennen",
        Delete = "Löschen",
        Open = "Öffnen",
        Cancel = "Abbrechen",
        Name = "Name",
        Title = "Titel",
        ModifiedTime = "Geändert",
        Size = "Größe",
        Loading = "Wird geladen …",
        EmptyFolder = "Klicken Sie auf „Erstellen“ oder ziehen Sie Dateien hierher.",
        DropHere = "Dateien zum Hochladen loslassen",
        ConfirmDelete = "{0} Element(e) in den Papierkorb verschieben?",
        Uploading = "{0} wird hochgeladen",
        NameTaken = "Ein Element mit diesem Namen gibt es schon.",
        InvalidName = "Dieser Name ist nicht erlaubt.",
        ChangedMeanwhile = "Das Element wurde inzwischen geändert. Die Liste ist aktualisiert.",
        NoPermission = "Dafür fehlt Ihnen die Berechtigung.",
        NotSignedIn = "Ihre Sitzung ist abgelaufen. Bitte neu anmelden.",
        GenericError = "Die Aktion ist fehlgeschlagen: {0}",
        SelectedCount = "{0} ausgewählt",
        Recent = "Zuletzt",
        RecycleBin = "Papierkorb",
        Search = "Suchen",
        SearchIn = "in {0}",
        SearchAll = "Überall suchen",
        SearchResults = "Suchergebnisse für „{0}“",
        NoResults = "Keine passenden Dateien oder Ordner.",
        NoRecent = "Keine kürzlich geänderten Dateien.",
        EmptyTrash = "Der Papierkorb ist leer.",
        Location = "Ort",
        DeletedTime = "Gelöscht",
        Restore = "Wiederherstellen",
        Restored = "{0} Element(e) am ursprünglichen Ort wiederhergestellt.",
        OpenLocation = "Speicherort öffnen",
        Versions = "Versionen",
        VersionsOf = "Versionen von {0}",
        Current = "aktuell",
        SavedBy = "Gespeichert von",
        Close = "Schließen",
        VersionRestored = "Die gewählte Version ist jetzt die aktuelle.",
        SignInTo = "Bei {0} anmelden",
        SignInButton = "Anmelden",
        SignInTagline = "SyncDrive – Ihre Dateien auf Ihrem eigenen Server",
        LoginPortal = "Anmeldeseite",
        ServerName = "Servername",
        LoginMessage = "Text unter dem Namen",
        Background = "Hintergrundbild",
        ChooseImage = "Bild auswählen …",
        RemoveImage = "Bild entfernen",
        ImageHint = "JPEG, PNG oder WebP, bis 5 MB.",
        ImageTooLarge = "Das Bild ist größer als 5 MB.",
        Save = "Speichern",
        Saved = "Gespeichert.",
        Starred = "Favoriten",
        AddStar = "Zu Favoriten hinzufügen",
        RemoveStar = "Aus Favoriten entfernen",
        NoStarred = "Klicken Sie auf den Stern neben einer Datei oder einem Ordner, um sie hier wiederzufinden.",
        Labels = "Labels",
        MyLabels = "Meine Labels",
        NewLabel = "Neues Label",
        Color = "Farbe",
        DeleteLabel = "Label löschen",
        ConfirmDeleteLabel = "Label „{0}“ löschen? Dateien und Ordner bleiben, wo sie sind; nur das Label verschwindet.",
        NoLabeled = "Noch tragen keine Dateien oder Ordner dieses Label.",
        NoLabels = "Noch keine Labels.",
        LabelExists = "Ein Label mit diesem Namen gibt es schon.",
        InvalidLabelName = "Bitte einen Namen mit höchstens 64 Zeichen eingeben.",
        Notifications = "Benachrichtigungen",
        NoNotifications = "Keine Benachrichtigungen.",
        MarkAllRead = "Alle als gelesen markieren",
        NotifyShared = "{0} hat „{1}“ mit Ihnen geteilt",
        StorageUsed = "{0} belegt",
        StorageUsedOf = "{0} von {1} belegt",
        ClientList = "Geräteliste",
        DeviceName = "Gerätename",
        ClientType = "Client-Typ",
        Version = "Version",
        LastConnection = "Letzte Verbindung",
        Unlink = "Trennen",
        Refresh = "Aktualisieren",
        ThisBrowser = "dieser Browser",
        ClientWeb = "Webbrowser",
        ClientDesktop = "Desktop-Client",
        ConfirmUnlink = "„{0}“ trennen? Das Gerät muss sich neu anmelden, bevor es wieder synchronisiert.",
        DeviceUnlinked = "„{0}“ wurde getrennt.",
        Categories = "Kategorien",
        Share = "Freigeben",
        ShareTitle = "„{0}“ freigeben",
        Permissions = "Berechtigungen",
        PublicLink = "Öffentlicher Link",
        PeopleWithAccess = "Eingeladene",
        NobodyYet = "Noch hat niemand Zugriff.",
        Invite = "Hinzufügen",
        Remove = "Entfernen",
        RolePreviewer = "Vorschau",
        RoleViewer = "Betrachter",
        RoleEditor = "Bearbeiter",
        RoleManager = "Verwalter",
        RoleOwner = "Besitzer",
        OnlyFolders = "Einladen geht nur für Ordner. Dateien teilen Sie per öffentlichem Link.",
        CreateLink = "Öffentlichen Link erstellen",
        LinkHint = "Jeder mit dem Link kann ansehen und herunterladen.",
        PasswordProtect = "Mit Passwort schützen",
        PasswordKept = "Ein Passwort ist gesetzt. Feld leer lassen, um es zu behalten.",
        ExpiryDate = "Ablaufdatum",
        CopyLink = "Link kopieren",
        LinkCopied = "Link kopiert: {0}",
        RequestFiles = "Dateien anfordern",
        RequestTitle = "Dateien für „{0}“ anfordern",
        CreateRequest = "Hochlade-Link erstellen",
        RequestHint = "Jeder mit dem Link kann Dateien in diesen Ordner hochladen, sieht aber nicht, was darin liegt.",
        FileRequest = "Dateianfrage",
        PasswordMissing = "Bitte ein Passwort eingeben.",
        PasswordTooShort = "Das Passwort braucht mindestens 4 Zeichen.",
        AlreadyHasAccess = "Dieser Benutzer hat schon Zugriff.",
        UnknownUser = "Einen Benutzer mit diesem Namen gibt es nicht.",
        NoSharePermission = "Dieses Element können Sie nicht freigeben.",
        SharedWithMe = "Mit mir geteilt",
        SharedWithOthers = "Von mir geteilt",
        FileRequests = "Dateianfragen",
        SharedBy = "Geteilt von",
        Access = "Zugriff",
        Sharing = "Freigabe",
        NothingSharedWithMe = "Noch hat niemand einen Ordner mit Ihnen geteilt.",
        NothingShared = "Sie haben noch nichts geteilt.",
        NoFileRequests = "Keine Dateianfragen. Rechtsklick auf einen Ordner und „Dateien anfordern“ wählen.",
        Created = "Erstellt",
        Expires = "Läuft ab",
        Status = "Status",
        Active = "Aktiv",
        Expired = "Abgelaufen",
        Never = "Nie",
        ManageSharing = "Freigabe verwalten …",
        PeopleCount = "{0} Person(en)",
        LeaveShare = "Verlassen",
        ConfirmLeave = "„{0}“ verlassen? Der Ordner verschwindet dann aus Ihrer Ansicht.",
        LinkExpired = "Dieser Link ist abgelaufen.",
        LinkNotFound = "Diesen Link gibt es nicht oder er ist nicht mehr freigegeben.",
        LinkExpiresOn = "Verfügbar bis {0}",
        PasswordPrompt = "Dieser Link ist geschützt. Bitte das Passwort eingeben.",
        WrongPassword = "Falsches Passwort.",
        Unlock = "Öffnen",
        DownloadAll = "Alles herunterladen",
        SharedByOwner = "Geteilt von {0}",
        RequestPageHint = "{0} bittet Sie, Dateien in „{1}“ hochzuladen.",
        ChooseFiles = "Dateien auswählen",
        DropToUpload = "Dateien hierher ziehen oder auswählen.",
        UploadedAs = "Hochgeladen: {0}",
        Administration = "Verwaltung",
        Users = "Benutzer",
        NewUser = "Neuer Benutzer",
        Administrator = "Administrator",
        DisabledUser = "Gesperrt",
        DisableUser = "Sperren",
        EnableUser = "Entsperren",
        UserSettings = "Einstellungen",
        ResetPassword = "Passwort neu setzen",
        NewPassword = "Neues Passwort",
        QuotaGb = "Speicherkontingent (GB)",
        Unlimited = "unbegrenzt",
        Storage = "Speicher",
        DevicesColumn = "Geräte",
        UsedOf = "{0} von {1}",
        InvalidQuota = "Das Kontingent muss null oder größer sein.",
        UserPasswordTooShort = "Das Passwort braucht mindestens 10 Zeichen.",
        UserCreated = "Benutzer „{0}“ angelegt.",
        CannotChangeSelf = "Das eigene Konto lässt sich nicht sperren, das eigene Admin-Recht nicht entziehen.",
        PasswordReset = "Neues Passwort für „{0}“ gesetzt. Alle seine Geräte müssen sich neu anmelden.",
        UserExists = "Einen Benutzer mit diesem Namen gibt es schon.",
        QuotaExceeded = "Das Speicherkontingent ist ausgeschöpft.",
        You = "Sie",
        Groups = "Gruppen",
        TeamFoldersTab = "Team-Ordner",
        NewGroup = "Neue Gruppe",
        DeleteGroup = "Gruppe löschen",
        ConfirmDeleteGroup = "Gruppe „{0}“ löschen? Ihre Mitglieder verlieren den Zugriff, den sie über diese Gruppe hatten.",
        MembersOf = "Mitglieder von „{0}“",
        NoGroups = "Noch keine Gruppen.",
        NewTeamFolder = "Neuer Team-Ordner",
        NoTeamFolders = "Noch keine Team-Ordner.",
        Members = "Mitglieder",
        MembersSummary = "{0} Benutzer, {1} Gruppe(n)",
        NoMembers = "Noch keine Mitglieder.",
        KindUser = "Benutzer",
        KindGroup = "Gruppe",
        ChooseMember = "Benutzer oder Gruppe wählen",
        NameTakenAdmin = "Dieser Name ist schon vergeben.",
        TooManyAttempts = "Zu viele Fehlversuche. Bitte in ein paar Minuten erneut versuchen.",
        PoliciesTab = "Richtlinien",
        AllowPublicLinks = "Öffentliche Links erlauben",
        AllowFileRequests = "Dateianfragen erlauben",
        RequirePassword = "Passwort vorschreiben",
        LimitDays = "Gültigkeit begrenzen auf (Tage)",
        PoliciesHint = "Das Abschalten einer Linkart sperrt auch schon verschickte Links. Passwort- und Gültigkeitsregeln gelten für neue oder geänderte Links.",
        InvalidDays = "Bitte zwischen 1 und 3650 Tagen angeben.",
        ExpiryTooLate = "Der Administrator erlaubt höchstens {0} Tage. Bitte ein früheres Datum wählen.",
        LinksDisabled = "Öffentliche Links hat der Administrator abgeschaltet.",
        PolicyEnforced = "vom Administrator vorgegeben",
    };

    public static WebTexts For(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "de" ? German : English;

    /// <summary>Meldung zu einem Serverfehler; unbekannte Codes mit der Servermeldung.</summary>
    public string For(DriveException error) => error.Code switch
    {
        ErrorCode.AlreadyExists => NameTaken,
        ErrorCode.InvalidPath => InvalidName,
        ErrorCode.RevisionConflict or ErrorCode.NotFound => ChangedMeanwhile,
        ErrorCode.Forbidden => NoPermission,
        ErrorCode.Unauthorized => NotSignedIn,
        ErrorCode.QuotaExceeded => QuotaExceeded,
        ErrorCode.TooManyAttempts => TooManyAttempts,
        _ => string.Format(CultureInfo.CurrentCulture, GenericError, error.Message),
    };

    /// <summary>Anzeigename einer Rolle (<c>viewer</c> → „Viewer“ / „Betrachter“).</summary>
    public string RoleName(string role) => role switch
    {
        "previewer" => RolePreviewer,
        "viewer" => RoleViewer,
        "editor" => RoleEditor,
        "manager" => RoleManager,
        "owner" => RoleOwner,
        _ => role,
    };

    /// <summary>„1,5 MB“ – Größen mit Basis 1024.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
