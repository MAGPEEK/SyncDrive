namespace SyncDrive.Server.App.Accounts;

/// <summary>
/// Rollen in einem Space, aufsteigend (Previewer, Viewer, Editor, …). Die Zahlenwerte sind fest,
/// weil sie gespeichert werden; Vergleiche (<c>role &gt;= Editor</c>) nutzen die Reihenfolge.
/// </summary>
public enum SpaceRole
{
    /// <summary>Nur Vorschau: sieht Struktur und Metadaten, kann Inhalte nicht herunterladen.</summary>
    Previewer = 10,

    Viewer = 20,

    Editor = 30,

    /// <summary>Darf zusätzlich teilen und fremde Sperren lösen.</summary>
    Manager = 40,

    Owner = 50,
}

/// <param name="QuotaBytes">Speicherkontingent der eigenen Spaces (aktuelle Dateien); null = unbegrenzt.</param>
public sealed record UserRecord(
    string Id, string Name, string PasswordHash, bool IsAdmin, bool Disabled, long? QuotaBytes = null, DateTimeOffset? CreatedAt = null);

/// <param name="TokenHash">SHA-256 des Tokens; der Token selbst wird nie gespeichert.</param>
/// <param name="ClientType">z. B. <c>windows</c>, <c>web</c> (Web „Client list“: Client Type).</param>
public sealed record DeviceRecord(
    string Id,
    string UserId,
    string Name,
    string TokenHash,
    DateTimeOffset CreatedAt,
    bool Revoked,
    string? ClientType = null,
    string? ClientVersion = null,
    DateTimeOffset? LastSeen = null);

public sealed record SpaceMembership(string SpaceId, string SpaceName, SpaceRole Role);

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string encodedHash);
}

public interface IAccountStore
{
    Task<bool> AnyUserAsync(CancellationToken ct);

    /// <summary>Sucht den Benutzer, Groß-/Kleinschreibung ignoriert.</summary>
    Task<UserRecord?> FindUserByNameAsync(string name, CancellationToken ct);

    Task<UserRecord?> FindUserByIdAsync(string userId, CancellationToken ct);

    Task AddUserAsync(UserRecord user, CancellationToken ct);

    /// <summary>Alle Benutzer, nach Namen sortiert (Admin-Konsole).</summary>
    Task<IReadOnlyList<UserRecord>> ListUsersAsync(CancellationToken ct);

    /// <summary>Schreibt Passwort-Hash, Admin-Recht, Sperre und Kontingent; der Name bleibt.</summary>
    Task UpdateUserAsync(UserRecord user, CancellationToken ct);

    /// <summary>Sperrt alle Geräte des Benutzers (nach Sperren oder neuem Passwort).</summary>
    Task RevokeAllDevicesAsync(string userId, CancellationToken ct);

    /// <summary>Kontingent des Besitzers eines Space; null, wenn keines gesetzt ist.</summary>
    Task<long?> GetOwnerQuotaAsync(string spaceId, CancellationToken ct);

    Task AddDeviceAsync(DeviceRecord device, CancellationToken ct);

    Task<(DeviceRecord Device, UserRecord User)?> FindDeviceByTokenHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>Nicht gesperrte Geräte des Benutzers.</summary>
    Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(string userId, CancellationToken ct);

    Task TouchDeviceAsync(string deviceId, DateTimeOffset seenAt, CancellationToken ct);

    /// <summary>Sperrt das Gerät, sofern es dem Benutzer gehört. False, wenn es nicht existiert oder fremd ist.</summary>
    Task<bool> RevokeDeviceAsync(string deviceId, string userId, CancellationToken ct);

    /// <summary>Legt den Space an und macht <paramref name="ownerUserId"/> zum Besitzer.</summary>
    Task CreateSpaceAsync(string spaceId, string name, string ownerUserId, CancellationToken ct);

    /// <summary>Fügt ein Mitglied hinzu oder ändert seine Rolle.</summary>
    Task SetMemberAsync(string spaceId, string userId, SpaceRole role, CancellationToken ct);

    Task<IReadOnlyList<SpaceMembership>> ListMembershipsAsync(string userId, CancellationToken ct);

    /// <summary>Wirksame Rolle: die höchste aus direkter Mitgliedschaft und den Gruppen des Benutzers; null ohne Zugriff.</summary>
    Task<SpaceRole?> GetRoleAsync(string userId, string spaceId, CancellationToken ct);

    /// <summary>Team-Ordner (M7): ein Space ohne Besitzer, den Administratoren anlegen und Benutzern oder Gruppen zuweisen.</summary>
    Task CreateTeamSpaceAsync(string spaceId, string name, CancellationToken ct);

    Task<IReadOnlyList<(string Id, string Name)>> ListTeamSpacesAsync(CancellationToken ct);

    Task RenameSpaceAsync(string spaceId, string name, CancellationToken ct);

    /// <summary>Direkte Mitglieder und Gruppen eines Space mit Rolle.</summary>
    Task<IReadOnlyList<SpaceMember>> ListSpaceMembersAsync(string spaceId, CancellationToken ct);

    Task SetGroupRoleAsync(string spaceId, string groupId, SpaceRole role, CancellationToken ct);

    Task<bool> RemoveMemberAsync(string spaceId, string userId, CancellationToken ct);

    Task<bool> RemoveGroupAsync(string spaceId, string groupId, CancellationToken ct);
}

/// <summary>Mitglied eines Space: ein Benutzer oder eine Gruppe.</summary>
/// <param name="Kind"><see cref="SpaceMember.User"/> oder <see cref="SpaceMember.Group"/>.</param>
public sealed record SpaceMember(string Kind, string Id, string Name, SpaceRole Role)
{
    public const string User = "user";
    public const string Group = "group";
}

public sealed record GroupRecord(string Id, string Name);

/// <summary>Benutzergruppen (M7): Rechte an Team-Ordnern gelten für alle Mitglieder.</summary>
public interface IGroupStore
{
    Task<bool> AddGroupAsync(GroupRecord group, CancellationToken ct);

    Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken ct);

    Task<GroupRecord?> FindGroupAsync(string groupId, CancellationToken ct);

    /// <summary>Löscht die Gruppe samt Mitgliedschaften und Rechten an Team-Ordnern.</summary>
    Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct);

    Task<IReadOnlyList<UserRecord>> ListGroupMembersAsync(string groupId, CancellationToken ct);

    Task SetGroupMemberAsync(string groupId, string userId, bool member, CancellationToken ct);
}

/// <summary>Favoriten und Labels gehören dem Benutzer, nicht dem Space; Knoten werden über ihre stabile Id gemerkt.</summary>
public sealed record LabelRecord(string Id, string UserId, string Name, string Color);

public sealed record NodeReference(string SpaceId, string NodeId);

public interface IUserItemStore
{
    Task SetStarAsync(string userId, NodeReference node, bool starred, CancellationToken ct);

    Task<IReadOnlyList<NodeReference>> ListStarsAsync(string userId, CancellationToken ct);

    Task SaveLabelAsync(LabelRecord label, CancellationToken ct);

    Task<bool> DeleteLabelAsync(string userId, string labelId, CancellationToken ct);

    Task<IReadOnlyList<LabelRecord>> ListLabelsAsync(string userId, CancellationToken ct);

    Task SetLabelAsync(string labelId, NodeReference node, bool assigned, CancellationToken ct);

    Task<IReadOnlyList<NodeReference>> ListLabeledAsync(string labelId, CancellationToken ct);
}

/// <param name="Type">Art, z. B. <c>unlock_request</c>, später <c>shared</c>.</param>
public sealed record NotificationRecord(
    string Id,
    string UserId,
    string Type,
    DateTimeOffset CreatedAt,
    string? ActorUserId,
    string? SpaceId,
    string? NodeId,
    string? Path,
    bool Read);

public interface INotificationStore
{
    Task AddAsync(NotificationRecord notification, CancellationToken ct);

    /// <summary>Neueste zuerst.</summary>
    Task<IReadOnlyList<NotificationRecord>> ListAsync(string userId, int limit, CancellationToken ct);

    Task<bool> MarkReadAsync(string userId, string notificationId, CancellationToken ct);
}
