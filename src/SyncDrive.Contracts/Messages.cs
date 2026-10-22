namespace SyncDrive.Contracts;

/// <summary>Body von <c>POST /spaces/{s}/commit</c>. Hashes als <c>sha256:&lt;hex&gt;</c>.</summary>
/// <param name="BaseRevision">Revision des Knotens, auf der der Client entschieden hat; 0, wenn er ihn nicht kannte.</param>
/// <param name="NewPath">Nur bei <c>move</c>: Ziel. <see cref="Path"/> ist dann die Quelle.</param>
public sealed record CommitRequest(
    string Op,
    string Path,
    long BaseRevision,
    string? NewPath = null,
    IReadOnlyList<string>? Blocks = null,
    long Size = 0,
    DateTimeOffset? MTime = null,
    string? FileHash = null);

public sealed record CommitResponse(long Revision, string NodeId);

/// <summary>Aktueller Zustand eines Knotens, z. B. als Beilage zu <c>revision_conflict</c>.</summary>
public sealed record NodeInfo(
    string NodeId,
    string Path,
    string Kind,
    long Size,
    DateTimeOffset MTime,
    string? Hash,
    long Revision,
    bool Deleted);

/// <summary>Knoten samt Blockliste (<c>GET /spaces/{s}/node?path=</c>); Grundlage für Download und Dreiwege-Vergleich.</summary>
/// <param name="Blocks">Geordnete Blockhashes der aktuellen Fassung; leer bei Ordnern, gelöschten Knoten und leeren Dateien.</param>
public sealed record NodeDetail(NodeInfo Node, IReadOnlyList<string> Blocks);

/// <summary>Ein Eintrag des Snapshots (<c>GET /spaces/{s}/snapshot?path=</c>, als NDJSON gestreamt).</summary>
public sealed record SnapshotEntry(
    string NodeId,
    string Path,
    string Kind,
    long Size,
    DateTimeOffset MTime,
    string? Hash,
    long Revision);

/// <summary>Ein Eintrag im Änderungsfeed.</summary>
public sealed record ChangeEntry(
    long Revision,
    string NodeId,
    string Op,
    string Path,
    string? OldPath,
    string Kind,
    long Size,
    DateTimeOffset? MTime,
    string? Hash);

public sealed record ChangesResponse(IReadOnlyList<ChangeEntry> Changes, bool HasMore, long NextCursor);

/// <summary>Neueste Revision eines Space (<c>GET /spaces/{s}/cursor</c>): Startpunkt des Feeds nach einem vollständigen Abgleich.</summary>
public sealed record CursorResponse(long Latest);

public sealed record MissingBlocksRequest(IReadOnlyList<string> Hashes);

public sealed record MissingBlocksResponse(IReadOnlyList<string> Missing);

/// <summary>Body von <c>POST /auth/devices</c>: Benutzer und Passwort werden nur hier übertragen.</summary>
public sealed record DeviceRegistrationRequest(
    string UserName, string Password, string DeviceName, string? ClientType = null, string? ClientVersion = null);

/// <summary>Antwort mit dem Geräte-Token; er wird nur dieses eine Mal im Klartext geliefert.</summary>
public sealed record DeviceRegistrationResponse(string DeviceId, string Token);

/// <param name="SharedBy">Bei einem mit dem Benutzer geteilten Ordner (Id <c>sh_…</c>, M6b) der Name dessen, der ihn geteilt hat; sonst null.</param>
public sealed record SpaceSummary(string Id, string Name, string Role, string? SharedBy = null);

public sealed record SpacesResponse(IReadOnlyList<SpaceSummary> Spaces);

/// <summary>Fehlerkörper (<c>application/problem+json</c>) mit stabilem <see cref="Code"/>.</summary>
public sealed record ProblemDetail(
    string Code,
    string Title,
    NodeInfo? Current = null,
    IReadOnlyList<string>? MissingBlocks = null);

/// <summary>Eine gespeicherte Fassung einer Datei (B3, „Frühere Versionen durchsuchen“).</summary>
/// <param name="Blocks">Blockliste der Version: der Client lädt damit eine frühere Version herunter („Browse previous versions“ → Speichern unter).</param>
public sealed record VersionInfo(
    long Revision, long Size, DateTimeOffset MTime, string? Hash, DateTimeOffset CreatedAt, string? CreatedBy, IReadOnlyList<string>? Blocks = null);

public sealed record VersionsResponse(IReadOnlyList<VersionInfo> Versions);

/// <summary>Body von <c>POST /spaces/{s}/versions/restore</c>: die Version wird zur neuen aktuellen Fassung.</summary>
public sealed record RestoreVersionRequest(string Path, long Revision, long BaseRevision);

/// <summary>Eintrag im Server-Papierkorb (Web „Recycle Bin“: Titel, gelöscht am, Ort).</summary>
public sealed record TrashEntry(string NodeId, string Name, string OriginalPath, string Kind, long Size, DateTimeOffset? DeletedAt);

public sealed record TrashResponse(IReadOnlyList<TrashEntry> Entries);

public sealed record RestoreTrashRequest(string NodeId);

/// <summary>Ein angemeldetes Gerät (Web „Client list“: Gerätename, Typ, Version, letzte Verbindung).</summary>
public sealed record DeviceInfo(
    string DeviceId, string Name, string? ClientType, string? ClientVersion, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen, bool IsCurrent);

public sealed record DevicesResponse(IReadOnlyList<DeviceInfo> Devices);

/// <summary>Body von <c>POST /spaces/{s}/members</c>; <c>Role</c>: previewer, viewer, editor, manager, owner.</summary>
public sealed record AddMemberRequest(string UserName, string Role);

/// <summary>Ein Element mit aktuellem Pfad (Favoriten, Labels); nach Umbenennen stimmt der Pfad, weil über die Knoten-Id aufgelöst wird.</summary>
/// <param name="Size">Größe, Änderungszeit und Revision für die Listen der Weboberfläche („Starred“, Labels).</param>
public sealed record ItemRef(
    string SpaceId, string NodeId, string Path, string Kind, long Size = 0, DateTimeOffset MTime = default, long Revision = 0);

public sealed record ItemsResponse(IReadOnlyList<ItemRef> Items);

public sealed record LabelInfo(string Id, string Name, string Color);

public sealed record LabelsResponse(IReadOnlyList<LabelInfo> Labels);

public sealed record CreateLabelRequest(string Name, string Color);

/// <param name="Type">Art der Benachrichtigung, z. B. <c>shared</c>.</param>
public sealed record NotificationInfo(
    string Id, string Type, DateTimeOffset CreatedAt, string? Actor, string? SpaceId, string? Path, bool Read);

public sealed record NotificationsResponse(IReadOnlyList<NotificationInfo> Notifications);

/// <summary>Belegter Speicher unterhalb eines Ordners (<c>GET /spaces/{s}/usage?path=</c>), für „Usage“ in den Client-Einstellungen.</summary>
public sealed record UsageResponse(long Bytes, long Files);

/// <summary>Body von <c>POST /web/session</c>: Anmeldung im Browser; der Server setzt daraufhin das Sitzungs-Cookie (ADR-11).</summary>
public sealed record WebSignInRequest(string UserName, string Password);

/// <summary>Wer im Browser angemeldet ist (<c>GET /web/session</c>).</summary>
public sealed record WebSessionInfo(string UserName, bool IsAdmin);

/// <summary>Treffer von <c>GET /spaces/{s}/search?q=&amp;path=</c> und <c>GET /spaces/{s}/recent</c> (höchstens 200).</summary>
public sealed record FindResponse(IReadOnlyList<SnapshotEntry> Entries);

/// <summary>Anmeldeseite (<c>GET /web/portal</c>, ohne Anmeldung): selbst gewählter Servername, eine Zeile Text, Adresse des Hintergrundbilds.</summary>
/// <param name="BackgroundUrl">Null ohne eigenes Bild; sonst mit Versionsangabe, damit Browser nach einem Wechsel das neue laden.</param>
public sealed record PortalInfo(string ServerName, string? LoginMessage, string? BackgroundUrl);

/// <summary>Body von <c>PUT /web/portal</c> (nur Administratoren).</summary>
public sealed record PortalUpdateRequest(string ServerName, string? LoginMessage);

/// <summary>Body von <c>POST /spaces/{s}/sharing/invitees?path=</c>: lädt einen Benutzer zu einem Ordner oder einer Datei ein (M6b).</summary>
/// <param name="Role"><c>previewer</c>, <c>viewer</c> oder <c>editor</c>.</param>
public sealed record InviteRequest(string UserName, string Role);

/// <summary>Ein Eingeladener; <c>ShareId</c> ist zugleich die Space-Id, unter der er den geteilten Ordner sieht.</summary>
public sealed record InviteeInfo(string ShareId, string UserName, string Role);

/// <summary>Body von <c>PUT /spaces/{s}/sharing/link?path=&amp;kind=</c>: Link anlegen oder ändern.</summary>
/// <param name="Password">Neues Passwort; null lässt ein vorhandenes unverändert.</param>
/// <param name="RemovePassword">Entfernt den Passwortschutz.</param>
public sealed record LinkRequest(DateTimeOffset? ExpiresAt, string? Password = null, bool RemovePassword = false);

/// <param name="Kind"><see cref="LinkKinds.Share"/> (ansehen und herunterladen) oder <see cref="LinkKinds.Request"/> (Dateianfrage: nur hochladen).</param>
/// <param name="Token">Teil der Adresse <c>/s/{token}</c>; nur der Ersteller und Verwalter des Space sehen ihn.</param>
public sealed record LinkInfo(string Id, string Kind, string Token, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool HasPassword);

/// <summary>Freigaben eines Elements (<c>GET /spaces/{s}/sharing?path=</c>): Eingeladene, öffentlicher Link, Dateianfrage.</summary>
public sealed record SharingResponse(IReadOnlyList<InviteeInfo> Invitees, LinkInfo? Link, LinkInfo? Request);

/// <summary>Ein geteiltes Element in „Shared with others“ (<c>GET /sharing/by-me</c>).</summary>
public sealed record SharedItem(
    string SpaceId, string NodeId, string Path, string Kind, IReadOnlyList<InviteeInfo> Invitees, LinkInfo? Link, LinkInfo? Request);

public sealed record SharedItemsResponse(IReadOnlyList<SharedItem> Items);

/// <summary>Was ein öffentlicher Link zeigt (<c>GET /public/{token}</c>, ohne Anmeldung).</summary>
/// <param name="Kind">Art des Links (<see cref="LinkKinds"/>).</param>
/// <param name="ItemKind">Datei oder Ordner (<see cref="NodeKinds"/>).</param>
/// <param name="PasswordRequired">Ein Passwort fehlt noch; vorher liefert der Link nur diese Angaben.</param>
public sealed record PublicLinkInfo(
    string Kind, string Name, string ItemKind, long Size, DateTimeOffset MTime, string? Owner, bool PasswordRequired, DateTimeOffset? ExpiresAt);

/// <summary>Body von <c>POST /public/{token}/password</c>.</summary>
public sealed record LinkPasswordRequest(string Password);

/// <summary>Antwort auf eine hochgeladene Datei einer Dateianfrage: der Name, unter dem sie abgelegt wurde.</summary>
public sealed record PublicUploadResponse(string Name, long Size);

/// <summary>Body von <c>POST /public/{token}/files</c>: Datei einer Dateianfrage aus vorher hochgeladenen Blöcken (Browser zerlegt selbst).</summary>
public sealed record PublicFileRequest(string Name, IReadOnlyList<string> Blocks, long Size, string FileHash);

/// <summary>Eigener Speicher (<c>GET /account/storage</c>): belegt in den eigenen Spaces und das Kontingent; null = unbegrenzt.</summary>
public sealed record StorageInfo(long UsedBytes, long? QuotaBytes);

/// <summary>Ein Benutzer in der Admin-Konsole (M7): Rechte, Sperre, belegter Speicher (eigene Spaces) und Kontingent, Anzahl Geräte.</summary>
public sealed record AdminUserInfo(
    string Id, string Name, bool IsAdmin, bool Disabled, long UsedBytes, long? QuotaBytes, int Devices, DateTimeOffset? CreatedAt);

public sealed record AdminUsersResponse(IReadOnlyList<AdminUserInfo> Users);

/// <summary>Body von <c>POST /admin/users</c>; <c>QuotaBytes</c> null = unbegrenzt.</summary>
public sealed record CreateUserRequest(string Name, string Password, bool IsAdmin = false, long? QuotaBytes = null);

/// <summary>Body von <c>PATCH /admin/users/{id}</c>: nur gesetzte Felder ändern sich; <c>ClearQuota</c> hebt das Kontingent auf.</summary>
public sealed record UpdateUserRequest(bool? IsAdmin = null, bool? Disabled = null, long? QuotaBytes = null, bool ClearQuota = false);

/// <summary>Body von <c>PUT /admin/users/{id}/password</c>: neues Passwort; alle Geräte des Benutzers werden abgemeldet.</summary>
public sealed record SetPasswordRequest(string Password);

/// <summary>Eine Benutzergruppe in der Verwaltung (M7).</summary>
public sealed record GroupInfo(string Id, string Name, IReadOnlyList<GroupMemberInfo> Members);

public sealed record GroupMemberInfo(string UserId, string UserName);

public sealed record GroupsResponse(IReadOnlyList<GroupInfo> Groups);

/// <summary>Body von <c>POST /admin/groups</c> und <c>POST /admin/team-folders</c>, <c>PATCH /admin/team-folders/{id}</c>.</summary>
public sealed record NameRequest(string Name);

/// <summary>Ein Team-Ordner in der Verwaltung: Mitglieder (Benutzer und Gruppen) mit Rolle, belegter Speicher.</summary>
public sealed record TeamFolderInfo(string SpaceId, string Name, long UsedBytes, IReadOnlyList<TeamMemberInfo> Members);

/// <param name="Kind"><c>user</c> oder <c>group</c>.</param>
/// <param name="Role"><c>previewer</c>, <c>viewer</c>, <c>editor</c> oder <c>manager</c>.</param>
public sealed record TeamMemberInfo(string Kind, string Id, string Name, string Role);

public sealed record TeamFoldersResponse(IReadOnlyList<TeamFolderInfo> TeamFolders);

/// <summary>Body von <c>PUT /admin/team-folders/{id}/members</c>: Benutzer oder Gruppe mit Rolle hinzufügen oder die Rolle ändern.</summary>
public sealed record TeamMemberRequest(string Kind, string Id, string Role);

/// <summary>
/// Freigabe-Richtlinien des Servers (M7, <c>GET /policies</c> für alle Angemeldeten, <c>PUT /admin/policies</c> für Administratoren).
/// Höchstdauern in Tagen; null = kein Ablaufdatum nötig.
/// </summary>
public sealed record SharingPolicies(
    bool PublicLinks = true,
    bool LinkPasswordRequired = false,
    int? LinkMaxDays = null,
    bool FileRequests = true,
    bool RequestPasswordRequired = false,
    int? RequestMaxDays = null);
