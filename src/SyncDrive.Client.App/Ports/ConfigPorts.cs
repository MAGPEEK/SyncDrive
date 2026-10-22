using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;

namespace SyncDrive.Client.App.Ports;

/// <summary>Verbindungen, Sync-Aufgaben und globale Einstellungen der Installation (<c>client.db</c>). Keine Geheimnisse (ADR-7).</summary>
public interface IClientConfigStore
{
    Task<IReadOnlyList<ConnectionInfo>> ListConnectionsAsync(CancellationToken ct);

    Task SaveConnectionAsync(ConnectionInfo connection, CancellationToken ct);

    /// <summary>Entfernt die Verbindung samt ihrer Aufgaben (lokale Dateien bleiben).</summary>
    Task RemoveConnectionAsync(string connectionId, CancellationToken ct);

    Task<IReadOnlyList<SyncTaskDefinition>> ListTasksAsync(CancellationToken ct);

    Task SaveTaskAsync(SyncTaskDefinition task, CancellationToken ct);

    Task RemoveTaskAsync(string taskId, CancellationToken ct);

    Task<ClientSettings> GetSettingsAsync(CancellationToken ct);

    Task SaveSettingsAsync(ClientSettings settings, CancellationToken ct);
}

/// <summary>Geräte-Token und Proxy-Passwort im Anmeldedatenspeicher des Betriebssystems (Windows: Credential Manager/DPAPI).</summary>
public interface ICredentialStore
{
    Task SaveAsync(string key, string secret, CancellationToken ct);

    Task<string?> GetAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}
