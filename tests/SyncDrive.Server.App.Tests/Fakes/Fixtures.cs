using System.Security.Cryptography;
using SyncDrive.Core.Model;
using SyncDrive.Infra.Blob;
using SyncDrive.Infra.Sqlite;
using SyncDrive.Server.App.Ports;
using SyncDrive.Testing;

namespace SyncDrive.Server.App.Tests.Fakes;

/// <summary>Bündelt einen Store und einen Blockspeicher, damit dieselben Vertragstests gegen jede Implementierung laufen.</summary>
public interface IStoreFixture : IDisposable
{
    ISpaceStore Store { get; }

    IBlockStore Blocks { get; }

    void AddSpace(string spaceId);

    /// <summary>Entfernt Feed-Einträge bis einschließlich <paramref name="upToRevision"/>.</summary>
    void TrimFeed(string spaceId, long upToRevision);

    ContentHash AddBlock(byte[] content);
}

public static class Fixtures
{
    public static ContentHash HashOf(byte[] content) => ContentHash.FromBytes(SHA256.HashData(content));
}

public sealed class InMemoryFixture : IStoreFixture
{
    private readonly InMemorySpaceStore _store = new();
    private readonly InMemoryBlockStore _blocks = new();

    public ISpaceStore Store => _store;

    public IBlockStore Blocks => _blocks;

    public void AddSpace(string spaceId) => _store.AddSpace(spaceId);

    public void TrimFeed(string spaceId, long upToRevision) => _store.Floor = upToRevision;

    public ContentHash AddBlock(byte[] content) => _blocks.Add(content);

    public void Dispose() => _store.Dispose();
}

public sealed class SqliteFixture : IStoreFixture
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "syncdrive-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSpaceStore _store;
    private readonly FileBlockStore _blocks;

    public SqliteFixture()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteSpaceStore(Path.Combine(_directory, "server.db"));
        _blocks = new FileBlockStore(Path.Combine(_directory, "blocks"));
    }

    public ISpaceStore Store => _store;

    public IBlockStore Blocks => _blocks;

    public void AddSpace(string spaceId) => _store.CreateSpaceAsync(spaceId, CancellationToken.None).GetAwaiter().GetResult();

    public void TrimFeed(string spaceId, long upToRevision) =>
        _store.TrimFeedAsync(spaceId, upToRevision, CancellationToken.None).GetAwaiter().GetResult();

    public ContentHash AddBlock(byte[] content)
    {
        var hash = Fixtures.HashOf(content);
        _blocks.WriteAsync(hash, new MemoryStream(content), CancellationToken.None).GetAwaiter().GetResult();
        return hash;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // gepoolte Verbindungen halten die Datei offen
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen im Temp-Ordner ist best effort.
        }
    }
}
