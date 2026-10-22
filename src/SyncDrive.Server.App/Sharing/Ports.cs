using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Server.App.Sharing;

/// <summary>Einladung eines Benutzers zu einem Knoten (Ordner oder Datei) eines Space (M6b, ADR-12). Die Id ist zugleich seine Space-Id.</summary>
public sealed record ShareRecord(
    string Id, string SpaceId, string NodeId, string UserId, SpaceRole Role, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// Öffentlicher Link auf einen Knoten: zum Ansehen (<c>share</c>) oder als Dateianfrage (<c>request</c>). Der Token steht im Klartext in der
/// Datenbank, weil der Ersteller den Link später wieder kopieren können soll; das Passwort nur als Hash.
/// </summary>
public sealed record LinkRecord(
    string Id,
    string Token,
    string Kind,
    string SpaceId,
    string NodeId,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string? PasswordHash);

public interface IShareStore
{
    /// <summary>Legt die Einladung an oder ändert die Rolle einer vorhandenen (je Knoten und Benutzer höchstens eine).</summary>
    Task<ShareRecord> SaveShareAsync(ShareRecord share, CancellationToken ct);

    Task<ShareRecord?> FindShareAsync(string shareId, CancellationToken ct);

    Task<IReadOnlyList<ShareRecord>> ListSharesForUserAsync(string userId, CancellationToken ct);

    Task<IReadOnlyList<ShareRecord>> ListSharesOfNodeAsync(string spaceId, string nodeId, CancellationToken ct);

    Task<IReadOnlyList<ShareRecord>> ListSharesCreatedByAsync(string userId, CancellationToken ct);

    Task<bool> DeleteShareAsync(string shareId, CancellationToken ct);

    /// <summary>Legt den Link an oder ersetzt den vorhandenen derselben Art am selben Knoten.</summary>
    Task SaveLinkAsync(LinkRecord link, CancellationToken ct);

    Task<LinkRecord?> FindLinkAsync(string linkId, CancellationToken ct);

    Task<LinkRecord?> FindLinkByTokenAsync(string token, CancellationToken ct);

    Task<IReadOnlyList<LinkRecord>> ListLinksOfNodeAsync(string spaceId, string nodeId, CancellationToken ct);

    Task<IReadOnlyList<LinkRecord>> ListLinksCreatedByAsync(string userId, CancellationToken ct);

    Task<bool> DeleteLinkAsync(string linkId, CancellationToken ct);
}
