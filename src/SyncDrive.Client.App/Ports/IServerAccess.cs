using SyncDrive.Contracts;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Ports;

/// <summary>Angemeldet: Serveradresse, Geräte-Token und ggf. das bestätigte Zertifikat.</summary>
public sealed record ServerLogin(Uri Server, string DeviceToken, string? PinnedCertificateSha256);

/// <param name="Trusted">Zertifikatskette vom System anerkannt (oder unverschlüsselt in Tests); sonst muss der Nutzer den Fingerabdruck bestätigen.</param>
public sealed record CertificateCheck(bool Trusted, string? CertificateSha256);

public sealed record DeviceRegistration(string DeviceId, string Token);

/// <summary>Proxy für alle Verbindungen zum Server (Einstellungen „Proxy“; Passwort aus dem Geheimnisspeicher).</summary>
public sealed record ProxyConfig(Settings.ProxyMode Mode, string? Host = null, int? Port = null, string? UserName = null, string? Password = null)
{
    public static ProxyConfig None { get; } = new(Settings.ProxyMode.None);
}

/// <summary>Was Assistent und Verwaltung vom Server brauchen (außerhalb des Sync-Laufs).</summary>
public interface IServerAccess
{
    /// <summary>Erreichbarkeit und Zertifikat; scheitert mit <c>Transient</c>, wenn niemand antwortet.</summary>
    Task<Result<CertificateCheck>> ProbeAsync(Uri server, CancellationToken ct);

    Task<Result<DeviceRegistration>> RegisterDeviceAsync(
        Uri server, string userName, string password, string deviceName, string? pinnedCertificateSha256, CancellationToken ct);

    Task<Result<IReadOnlyList<SpaceSummary>>> ListSpacesAsync(ServerLogin login, CancellationToken ct);

    Task<Result<IReadOnlyList<NotificationInfo>>> ListNotificationsAsync(ServerLogin login, CancellationToken ct);

    Task<Result<bool>> MarkNotificationReadAsync(ServerLogin login, string notificationId, CancellationToken ct);

    Task<Result<UsageResponse>> GetUsageAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct);

    Task<Result<IReadOnlyList<VersionInfo>>> ListVersionsAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct);

    Task<Result<CommitResponse>> RestoreVersionAsync(
        ServerLogin login, string spaceId, RelativePath path, long revision, long baseRevision, CancellationToken ct);

    /// <summary>Knoten mit Revision und Sperre; null, wenn es ihn nicht gibt.</summary>
    Task<Result<NodeInfo?>> GetNodeInfoAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct);

    Task<Result<bool>> LockAsync(ServerLogin login, string spaceId, RelativePath path, LockAction action, CancellationToken ct);

    /// <summary>Inhalt eines Blocks; der Aufrufer gibt den Strom frei.</summary>
    Task<Result<Stream>> DownloadBlockAsync(ServerLogin login, string spaceId, string hash, CancellationToken ct);

    /// <summary>Direkter Inhalt eines Ordners (eine Ebene).</summary>
    Task<Result<IReadOnlyList<SnapshotEntry>>> ListChildrenAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct);
}

/// <summary>Sperren (A15): selbst sperren, entsperren (Sperrender oder Manager), beim Sperrenden um Entsperren bitten.</summary>
public enum LockAction
{
    Lock,
    Unlock,
    RequestUnlock,
}
