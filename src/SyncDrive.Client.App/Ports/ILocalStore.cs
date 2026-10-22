using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Ports;

/// <param name="IsCloudOnly">Platzhalter ohne lokalen Inhalt (On-Demand): Größe und Datum stimmen, Lesen würde ihn erst herunterladen.</param>
public sealed record LocalEntry(RelativePath Path, NodeKind Kind, long Size, DateTimeOffset MTime, bool IsCloudOnly = false);

public enum SkipReason
{
    /// <summary>Name ist für den Sync nicht darstellbar (reservierter Name, Sonderzeichen, zu lang …).</summary>
    InvalidName,

    /// <summary>Symbolische Verknüpfung oder Reparse-Punkt; wird nicht verfolgt (Schleifengefahr).</summary>
    Link,

    /// <summary>Nicht lesbar (Rechte, gesperrt).</summary>
    Unreadable,
}

/// <summary>Ergebnis eines Scan-Schritts: ein Eintrag oder ein bewusst übersprungener Pfad samt Grund (für den Verlauf).</summary>
public abstract record ScanItem
{
    private ScanItem()
    {
    }

    public sealed record Found(LocalEntry Entry) : ScanItem;

    public sealed record Skipped(string RawPath, SkipReason Reason) : ScanItem;
}

/// <summary>Wohin eine Löschung gesichert wurde (A14).</summary>
public sealed record TrashItem(string TrashPath, DateTimeOffset DeletedAt);

/// <summary>Eine noch unsichtbare Zieldatei. Erst <see cref="CommitAsync"/> macht sie atomar am Zielpfad sichtbar.</summary>
public interface IStagedFile : IAsyncDisposable
{
    /// <summary>Hierhin wird der Inhalt geschrieben.</summary>
    Stream Content { get; }

    /// <summary>Schließt den Strom, setzt die Änderungszeit und ersetzt das Ziel atomar. Ohne Commit bleibt nichts zurück.</summary>
    Task CommitAsync(DateTimeOffset mtime, CancellationToken ct);
}

/// <summary>Der Sync-Ordner auf dem Gerät. Alle Pfade sind validierte <see cref="RelativePath"/>-Werte.</summary>
public interface ILocalStore
{
    /// <summary>Durchläuft den Teilbaum ab <paramref name="root"/> (ohne den internen Ordner) und meldet Übersprungenes.</summary>
    IAsyncEnumerable<ScanItem> ScanAsync(RelativePath root, CancellationToken ct);

    Task<LocalEntry?> StatAsync(RelativePath path, CancellationToken ct);

    Task<Stream> OpenReadAsync(RelativePath path, CancellationToken ct);

    /// <summary>Legt die Staging-Datei an; fehlende Zielordner entstehen erst beim Commit.</summary>
    Task<IStagedFile> StageAsync(RelativePath target, CancellationToken ct);

    Task CreateDirectoryAsync(RelativePath path, DateTimeOffset mtime, CancellationToken ct);

    /// <summary>Verschiebt Datei oder Ordner in den Papierkorb des Sync-Ordners. Null, wenn nichts da war.</summary>
    Task<TrashItem?> MoveToTrashAsync(RelativePath path, CancellationToken ct);

    /// <summary>
    /// Räumt den Papierkorb auf: entfernt Stapel, die älter als <paramref name="maxAge"/> sind, danach die ältesten, bis höchstens
    /// <paramref name="maxBytes"/> belegt sind. Liefert die Zahl entfernter Stapel.
    /// </summary>
    Task<int> CleanTrashAsync(TimeSpan maxAge, long maxBytes, CancellationToken ct);

    /// <summary>Benennt um oder verschiebt; legt fehlende Zielordner an. Überschreibt nie.</summary>
    Task MoveAsync(RelativePath source, RelativePath target, CancellationToken ct);
}
