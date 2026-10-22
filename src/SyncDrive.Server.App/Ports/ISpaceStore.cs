using SyncDrive.Core.Model;

namespace SyncDrive.Server.App.Ports;

/// <summary>Persistenz eines Dateibaums samt Änderungsfeed. Alle Änderungen laufen über <see cref="InTransactionAsync{T}"/>.</summary>
public interface ISpaceStore
{
    Task<bool> SpaceExistsAsync(string spaceId, CancellationToken ct);

    /// <summary>
    /// Führt <paramref name="work"/> atomar und gegen andere Schreiber im selben Space serialisiert aus.
    /// Wirft <paramref name="work"/>, wird nichts übernommen.
    /// </summary>
    Task<T> InTransactionAsync<T>(string spaceId, Func<ISpaceTransaction, Task<T>> work, CancellationToken ct);

    Task<ChangePage> ReadChangesAsync(string spaceId, long after, int limit, CancellationToken ct);
}

public interface ISpaceTransaction
{
    /// <summary>Sucht ein Kind unter <paramref name="parentId"/> (null = Wurzel), Groß-/Kleinschreibung ignoriert; Grabsteine eingeschlossen.</summary>
    Task<NodeRecord?> FindChildAsync(string? parentId, string name);

    Task<NodeRecord?> FindByIdAsync(string id);

    /// <summary>Nicht gelöschte Kinder.</summary>
    Task<IReadOnlyList<NodeRecord>> ListLiveChildrenAsync(string? parentId);

    /// <summary>
    /// Welche der Blöcke irgendeine Datei oder Version dieses Space (auch gelöscht) verwendet. Nur solche gibt der Space heraus (M7, Härtung):
    /// wer den Hash eines fremden Blocks kennt, bekommt ihn nicht und erfährt auch nicht, ob es ihn gibt.
    /// </summary>
    Task<IReadOnlySet<ContentHash>> FindReferencedBlocksAsync(IReadOnlyCollection<ContentHash> hashes);

    /// <summary>Summe der Größen aller nicht gelöschten Dateien (Speicherkontingent); ohne Versionen und Papierkorb.</summary>
    Task<long> SumLiveFileSizesAsync();

    /// <summary>Legt an oder ersetzt den Knoten mit gleicher <see cref="NodeRecord.Id"/>.</summary>
    Task SaveAsync(NodeRecord node);

    /// <summary>Entfernt einen Grabstein, dessen Name neu vergeben wird.</summary>
    Task RemoveAsync(string nodeId);

    /// <summary>Vergibt die nächste Revision des Space (lückenlos, monoton).</summary>
    Task<long> AllocateRevisionAsync();

    Task AppendChangeAsync(ChangeRecord change);

    Task<string?> GetIdempotentResponseAsync(string key);

    Task PutIdempotentResponseAsync(string key, string response);

    /// <summary>Alle Grabsteine des Space (für den Papierkorb).</summary>
    Task<IReadOnlyList<NodeRecord>> ListDeletedAsync();

    /// <summary>Grabsteine direkt unter <paramref name="parentId"/>, die in derselben Revision gelöscht wurden (Wiederherstellen eines Teilbaums).</summary>
    Task<IReadOnlyList<NodeRecord>> ListDeletedChildrenAsync(string parentId, long revision);

    Task AddVersionAsync(NodeVersion version);

    /// <summary>Versionen eines Knotens, neueste zuerst.</summary>
    Task<IReadOnlyList<NodeVersion>> ListVersionsAsync(string nodeId);

    Task RemoveVersionAsync(string nodeId, long revision);
}
