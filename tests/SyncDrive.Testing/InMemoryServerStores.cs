using System.Security.Cryptography;
using SyncDrive.Core.Model;
using SyncDrive.Server.App.Ports;

namespace SyncDrive.Testing;

/// <summary>Speicher im Arbeitsspeicher; Transaktionen arbeiten auf einer Kopie und übernehmen sie erst bei Erfolg.</summary>
public sealed class InMemorySpaceStore : ISpaceStore, IDisposable
{
    private readonly Dictionary<string, SpaceData> _spaces = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);

    public long Floor { get; set; }

    public void Dispose() => _writer.Dispose();

    public void AddSpace(string spaceId) => _spaces[spaceId] = new SpaceData();

    /// <summary>Simuliert einen gelöschten Space bzw. entzogenen Zugriff.</summary>
    public void RemoveSpace(string spaceId) => _spaces.Remove(spaceId);

    public Task<bool> SpaceExistsAsync(string spaceId, CancellationToken ct) =>
        Task.FromResult(_spaces.ContainsKey(spaceId));

    /// <summary>
    /// Wie der SQLite-Store: Transaktionen laufen nacheinander. Ohne diese Sperre überschreibt eine parallel laufende (auch nur lesende)
    /// Transaktion beim Zurückschreiben ihrer Kopie die Änderungen einer anderen – in Mehr-Client-Tests gingen so Uploads verloren.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(
        string spaceId, Func<ISpaceTransaction, Task<T>> work, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            SpaceData working;
            lock (_gate)
            {
                working = _spaces[spaceId].Clone();
            }

            var result = await work(new Transaction(working));
            lock (_gate)
            {
                _spaces[spaceId] = working;
            }

            return result;
        }
        finally
        {
            _writer.Release();
        }
    }

    public Task<ChangePage> ReadChangesAsync(string spaceId, long after, int limit, CancellationToken ct)
    {
        var data = _spaces[spaceId];
        var matching = data.Changes.Where(c => c.Revision > after).OrderBy(c => c.Revision).ToList();
        return Task.FromResult(new ChangePage([.. matching.Take(limit)], matching.Count > limit, Floor, data.LastRevision));
    }

    private sealed class SpaceData
    {
        public Dictionary<string, NodeRecord> Nodes { get; private init; } = [];

        public List<ChangeRecord> Changes { get; private init; } = [];

        public Dictionary<string, string> Idempotency { get; private init; } = [];

        public List<NodeVersion> Versions { get; private init; } = [];

        public long LastRevision { get; set; }

        public SpaceData Clone() => new()
        {
            Nodes = new(Nodes),
            Changes = [.. Changes],
            Idempotency = new(Idempotency),
            Versions = [.. Versions],
            LastRevision = LastRevision,
        };
    }

    private sealed class Transaction(SpaceData data) : ISpaceTransaction
    {
        public Task<NodeRecord?> FindChildAsync(string? parentId, string name) =>
            Task.FromResult(data.Nodes.Values.SingleOrDefault(n =>
                n.ParentId == parentId && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<NodeRecord?> FindByIdAsync(string id) => Task.FromResult(data.Nodes.GetValueOrDefault(id));

        public Task<IReadOnlySet<ContentHash>> FindReferencedBlocksAsync(IReadOnlyCollection<ContentHash> hashes)
        {
            var used = data.Nodes.Values.SelectMany(n => n.Blocks).Concat(data.Versions.SelectMany(v => v.Blocks)).ToHashSet();
            return Task.FromResult<IReadOnlySet<ContentHash>>(hashes.Where(used.Contains).ToHashSet());
        }

        public Task<long> SumLiveFileSizesAsync() =>
            Task.FromResult(data.Nodes.Values.Where(n => !n.Deleted && n.Kind == NodeKind.File).Sum(n => n.Size));

        public Task<IReadOnlyList<NodeRecord>> ListLiveChildrenAsync(string? parentId) =>
            Task.FromResult<IReadOnlyList<NodeRecord>>(
                [.. data.Nodes.Values.Where(n => n.ParentId == parentId && !n.Deleted)]);

        public Task SaveAsync(NodeRecord node)
        {
            data.Nodes[node.Id] = node;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string nodeId)
        {
            data.Nodes.Remove(nodeId);
            return Task.CompletedTask;
        }

        public Task<long> AllocateRevisionAsync() => Task.FromResult(++data.LastRevision);

        public Task AppendChangeAsync(ChangeRecord change)
        {
            // Wie der Primärschlüssel in SQLite: eine Revision, ein Feed-Eintrag.
            if (data.Changes.Any(c => c.Revision == change.Revision))
            {
                throw new InvalidOperationException($"Feed-Revision {change.Revision} ist bereits vergeben.");
            }

            data.Changes.Add(change);
            return Task.CompletedTask;
        }

        public Task<string?> GetIdempotentResponseAsync(string key) =>
            Task.FromResult(data.Idempotency.GetValueOrDefault(key));

        public Task PutIdempotentResponseAsync(string key, string response)
        {
            data.Idempotency[key] = response;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NodeRecord>> ListDeletedAsync() =>
            Task.FromResult<IReadOnlyList<NodeRecord>>([.. data.Nodes.Values.Where(n => n.Deleted)]);

        public Task<IReadOnlyList<NodeRecord>> ListDeletedChildrenAsync(string parentId, long revision) =>
            Task.FromResult<IReadOnlyList<NodeRecord>>(
                [.. data.Nodes.Values.Where(n => n.ParentId == parentId && n.Deleted && n.Revision == revision)]);

        public Task AddVersionAsync(NodeVersion version)
        {
            data.Versions.Add(version);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NodeVersion>> ListVersionsAsync(string nodeId) =>
            Task.FromResult<IReadOnlyList<NodeVersion>>([.. data.Versions.Where(v => v.NodeId == nodeId).OrderByDescending(v => v.Revision)]);

        public Task RemoveVersionAsync(string nodeId, long revision)
        {
            data.Versions.RemoveAll(v => v.NodeId == nodeId && v.Revision == revision);
            return Task.CompletedTask;
        }
    }
}

public sealed class InMemoryBlockStore : IBlockStore
{
    private readonly Dictionary<ContentHash, byte[]> _blocks = [];

    public static ContentHash HashOf(byte[] content) => ContentHash.FromBytes(SHA256.HashData(content));

    public ContentHash Add(byte[] content)
    {
        var hash = HashOf(content);
        _blocks[hash] = content;
        return hash;
    }

    public Task<long?> GetSizeAsync(ContentHash hash, CancellationToken ct) =>
        Task.FromResult(_blocks.TryGetValue(hash, out var b) ? (long?)b.Length : null);

    public async Task<BlockWriteResult> WriteAsync(ContentHash expected, Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (HashOf(buffer.ToArray()) != expected)
        {
            return BlockWriteResult.HashMismatch;
        }

        _blocks[expected] = buffer.ToArray();
        return BlockWriteResult.Stored;
    }

    public Task<Stream?> OpenReadAsync(ContentHash hash, CancellationToken ct) =>
        Task.FromResult<Stream?>(_blocks.TryGetValue(hash, out var b) ? new MemoryStream(b) : null);
}
