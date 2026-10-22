using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Server.App.Ports;

public enum ChangeOp
{
    Upsert,
    Delete,
    Move,

    /// <summary>Sperre gesetzt oder aufgehoben; Inhalt und Knoten-Revision bleiben unverändert.</summary>
    Lock,
}

/// <summary>
/// Knoten im Dateibaum. <see cref="Id"/> bleibt beim Umbenennen und Verschieben gleich. Gelöschte Knoten bleiben
/// als Grabstein erhalten, damit Clients mit altem Stand den Konflikt erkennen.
/// </summary>
/// <param name="ParentId">Null: direkt unter der Wurzel des Space.</param>
/// <param name="Revision">Revision der letzten Änderung an diesem Knoten (auch Löschen; nicht Sperren).</param>
/// <param name="LockedBy">Benutzer, der die Datei gesperrt hat (A15); nur er darf sie ändern, verschieben oder löschen.</param>
/// <param name="DeletedAt">Server-Zeit des Löschens (Papierkorb).</param>
public sealed record NodeRecord(
    string Id,
    string? ParentId,
    string Name,
    NodeKind Kind,
    bool Deleted,
    long Revision,
    long Size,
    DateTimeOffset MTime,
    ContentHash? Hash,
    IReadOnlyList<ContentHash> Blocks,
    string? LockedBy = null,
    DateTimeOffset? LockedAt = null,
    DateTimeOffset? DeletedAt = null);

public sealed record ChangeRecord(
    long Revision,
    string NodeId,
    ChangeOp Op,
    RelativePath Path,
    RelativePath? OldPath,
    NodeKind Kind,
    long Size,
    DateTimeOffset? MTime,
    ContentHash? Hash);

/// <param name="Floor">Höchste Revision, die aus dem Feed entfernt wurde (0: nichts); Cursor darunter sind abgelaufen.</param>
/// <param name="Latest">Neueste vergebene Revision.</param>
public sealed record ChangePage(IReadOnlyList<ChangeRecord> Changes, bool HasMore, long Floor, long Latest);

/// <summary>Eine gespeicherte Fassung einer Datei (B3). Die aktuelle Fassung ist ebenfalls eine Version.</summary>
/// <param name="CreatedBy">Benutzer, der sie erzeugt hat; null bei Systemvorgängen.</param>
public sealed record NodeVersion(
    string NodeId,
    long Revision,
    long Size,
    DateTimeOffset MTime,
    ContentHash? Hash,
    IReadOnlyList<ContentHash> Blocks,
    DateTimeOffset CreatedAt,
    string? CreatedBy);
