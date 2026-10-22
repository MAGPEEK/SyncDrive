using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Ports;

/// <summary>
/// Platzhalter im Sync-Ordner (Windows: Cloud-Files-API, ADR-10). Ein Platzhalter zeigt Name, Größe und Datum einer Serverdatei ohne ihren
/// Inhalt; der kommt erst beim Öffnen (<see cref="Hydrator"/>). Am In-Sync-Zustand zeigt der Explorer das Statussymbol.
/// </summary>
public interface IPlaceholders
{
    /// <summary>Legt einen Platzhalter ohne Inhalt an oder ersetzt einen vorhandenen, ebenfalls inhaltslosen. <paramref name="hash"/> merkt sich die Fassung.</summary>
    Task CreateAsync(RelativePath path, long size, DateTimeOffset mtime, ContentHash hash, CancellationToken ct);

    /// <summary>Markiert Datei oder Ordner als abgeglichen oder nicht; eine gewöhnliche Datei wird dabei zum (vollständigen) Platzhalter.</summary>
    Task SetInSyncAsync(RelativePath path, bool inSync, CancellationToken ct);
}

/// <summary>Ein mit der Plattform verbundener Sync-Ordner. <see cref="Connection"/> beenden trennt ihn (Platzhalter bleiben).</summary>
public sealed record CloudFolder(IPlaceholders Placeholders, IAsyncDisposable Connection);

/// <summary>Sync-Ordner als Sync-Root der Plattform: Statussymbole für alle Aufgaben, Platzhalter und Inhalt auf Abruf für On-Demand-Aufgaben.</summary>
public interface ICloudFolders
{
    /// <summary>Registriert den Ordner (einmalig) und verbindet ihn; Öffnen eines Platzhalters holt den Inhalt über <paramref name="hydrator"/>.</summary>
    Task<CloudFolder> AttachAsync(SyncTaskDefinition task, Hydrator hydrator, CancellationToken ct);

    /// <summary>
    /// Aufgabe gelöscht: Platzhalter ohne Inhalt verschwinden (ihr Inhalt liegt auf dem Server), alle anderen werden wieder gewöhnliche Dateien,
    /// die Registrierung wird entfernt. Nur nach dem Anhalten der Aufgabe aufrufen, sonst hielte der Sync das Entfernen für Löschungen.
    /// </summary>
    Task DetachAsync(SyncTaskDefinition task, CancellationToken ct);

    /// <summary>
    /// „Immer auf diesem Gerät behalten“ (<paramref name="keep"/>) oder „Speicherplatz freigeben“ für eine Datei oder einen Ordner samt Inhalt.
    /// Gesetzt wird nur der Wunsch; Holen und Freigeben übernimmt die verbundene Aufgabe.
    /// </summary>
    Task SetPinnedAsync(string localPath, bool keep, CancellationToken ct);
}
