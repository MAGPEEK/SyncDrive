using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Server.App.Ports;

namespace SyncDrive.Infra.Sqlite;

/// <summary>
/// SQLite-Implementierung von <see cref="ISpaceStore"/> (WAL). Schreibende Transaktionen eines Space laufen
/// nacheinander (Semaphor je Space), damit Revisionen lückenlos und geordnet vergeben werden.
/// </summary>
public sealed class SqliteSpaceStore : ISpaceStore
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SqliteSpaceStore(string databasePath)
    {
        _connectionString = SqliteDatabase.Initialize(databasePath);
        SqliteDatabase.RunOnce(_connectionString, "block_ref_v1", BackfillBlockReferences);
    }

    /// <summary>Datenbanken von vor 0.15.0: Blockverweise aus allen Knoten und Versionen nachtragen.</summary>
    private static void BackfillBlockReferences(SqliteConnection connection, SqliteTransaction transaction)
    {
        var references = new HashSet<(string Space, string Hash)>();
        foreach (var table in new[] { "node", "version" })
        {
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = $"SELECT space_id, blocks FROM {table} WHERE blocks <> ''";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                foreach (var hash in reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    references.Add((reader.GetString(0), hash));
                }
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO block_ref (space_id, hash) VALUES ($s, $h)";
        var space = insert.Parameters.Add("$s", SqliteType.Text);
        var hashParameter = insert.Parameters.Add("$h", SqliteType.Text);
        foreach (var (spaceId, hash) in references)
        {
            space.Value = spaceId;
            hashParameter.Value = hash;
            insert.ExecuteNonQuery();
        }
    }

    public async Task CreateSpaceAsync(string spaceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, "INSERT OR IGNORE INTO space (id) VALUES ($id)", ("$id", spaceId));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Entfernt Feed-Einträge bis einschließlich <paramref name="upToRevision"/>; ältere Cursor laufen dann ab.</summary>
    public async Task TrimFeedAsync(string spaceId, long upToRevision, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using (var delete = Command(connection, "DELETE FROM change WHERE space_id = $s AND revision <= $r",
                         ("$s", spaceId), ("$r", upToRevision)))
        {
            delete.Transaction = transaction;
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using (var floor = Command(connection,
                         "UPDATE space SET feed_floor = MAX(feed_floor, $r) WHERE id = $s", ("$s", spaceId), ("$r", upToRevision)))
        {
            floor.Transaction = transaction;
            await floor.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<bool> SpaceExistsAsync(string spaceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, "SELECT 1 FROM space WHERE id = $id", ("$id", spaceId));
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<T> InTransactionAsync<T>(
        string spaceId, Func<ISpaceTransaction, Task<T>> work, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(spaceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var result = await work(new Transaction(connection, transaction, spaceId));
            await transaction.CommitAsync(ct);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ChangePage> ReadChangesAsync(string spaceId, long after, int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        long floor;
        long latest;
        await using (var space = Command(connection, "SELECT feed_floor, last_revision FROM space WHERE id = $s", ("$s", spaceId)))
        await using (var reader = await space.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            floor = reader.GetInt64(0);
            latest = reader.GetInt64(1);
        }

        var changes = new List<ChangeRecord>();
        await using var query = Command(connection, """
            SELECT revision, node_id, op, path, old_path, kind, size, mtime, hash
            FROM change WHERE space_id = $s AND revision > $after ORDER BY revision LIMIT $limit
            """, ("$s", spaceId), ("$after", after), ("$limit", limit + 1));
        await using var rows = await query.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            changes.Add(ReadChange(rows));
        }

        return new ChangePage([.. changes.Take(limit)], changes.Count > limit, floor, latest);
    }

    private static ChangeRecord ReadChange(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), (ChangeOp)r.GetInt32(2), RelativePath.Parse(r.GetString(3)),
        r.IsDBNull(4) ? null : RelativePath.Parse(r.GetString(4)), (NodeKind)r.GetInt32(5), r.GetInt64(6),
        r.IsDBNull(7) ? null : ParseTime(r.GetString(7)), r.IsDBNull(8) ? null : ContentHash.Parse(r.GetString(8)));

    private static NodeRecord ReadNode(SqliteDataReader r) => new(
        r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), (NodeKind)r.GetInt32(3),
        r.GetInt32(4) != 0, r.GetInt64(5), r.GetInt64(6), ParseTime(r.GetString(7)),
        r.IsDBNull(8) ? null : ContentHash.Parse(r.GetString(8)),
        Blocks(r.GetString(9)),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : ParseTime(r.GetString(11)),
        r.IsDBNull(12) ? null : ParseTime(r.GetString(12)));

    private static ContentHash[] Blocks(string text) =>
        [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ContentHash.Parse)];

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FormatTime(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static string Key(string name) => name.ToUpperInvariant();

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var pragma = Command(connection, "PRAGMA busy_timeout = 5000;");
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private sealed class Transaction(SqliteConnection connection, SqliteTransaction transaction, string spaceId)
        : ISpaceTransaction
    {
        private const string NodeColumns = "id, parent_id, name, kind, deleted, revision, size, mtime, hash, blocks, locked_by, locked_at, deleted_at";

        public Task<NodeRecord?> FindChildAsync(string? parentId, string name) => QueryNodeAsync(
            $"SELECT {NodeColumns} FROM node WHERE space_id = $s AND parent_key = $p AND name_key = $k",
            ("$p", parentId ?? string.Empty), ("$k", Key(name)));

        public Task<NodeRecord?> FindByIdAsync(string id) => QueryNodeAsync(
            $"SELECT {NodeColumns} FROM node WHERE space_id = $s AND id = $id", ("$id", id));

        public async Task<IReadOnlyList<NodeRecord>> ListLiveChildrenAsync(string? parentId)
        {
            await using var command = Prepare(
                $"SELECT {NodeColumns} FROM node WHERE space_id = $s AND parent_key = $p AND deleted = 0",
                ("$p", parentId ?? string.Empty));
            await using var reader = await command.ExecuteReaderAsync();
            var nodes = new List<NodeRecord>();
            while (await reader.ReadAsync())
            {
                nodes.Add(ReadNode(reader));
            }

            return nodes;
        }

        public async Task SaveAsync(NodeRecord node)
        {
            await UpsertNodeAsync(node);
            await AddReferencesAsync(node.Blocks);
        }

        public async Task AddVersionAsync(NodeVersion version)
        {
            await InsertVersionAsync(version);
            await AddReferencesAsync(version.Blocks);
        }

        public async Task<IReadOnlySet<ContentHash>> FindReferencedBlocksAsync(IReadOnlyCollection<ContentHash> hashes)
        {
            var found = new HashSet<ContentHash>();
            foreach (var batch in hashes.Distinct().Chunk(400))
            {
                var names = batch.Select((_, i) => "$h" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
                await using var command = Prepare(
                    $"SELECT hash FROM block_ref WHERE space_id = $s AND hash IN ({string.Join(",", names)})",
                    [.. batch.Select((h, i) => (names[i], (object?)h.Value))]);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    found.Add(ContentHash.Parse(reader.GetString(0)));
                }
            }

            return found;
        }

        private async Task AddReferencesAsync(IEnumerable<ContentHash> blocks)
        {
            foreach (var hash in blocks.Distinct())
            {
                await ExecuteAsync("INSERT OR IGNORE INTO block_ref (space_id, hash) VALUES ($s, $h)", ("$h", hash.Value));
            }
        }

        private Task UpsertNodeAsync(NodeRecord node) => ExecuteAsync("""
            INSERT INTO node (space_id, id, parent_id, parent_key, name, name_key, kind, deleted, revision, size, mtime, hash, blocks, locked_by, locked_at, deleted_at)
            VALUES ($s, $id, $parent, $pk, $name, $nk, $kind, $deleted, $rev, $size, $mtime, $hash, $blocks, $lby, $lat, $dat)
            ON CONFLICT (space_id, id) DO UPDATE SET
                parent_id = $parent, parent_key = $pk, name = $name, name_key = $nk, kind = $kind, deleted = $deleted,
                revision = $rev, size = $size, mtime = $mtime, hash = $hash, blocks = $blocks,
                locked_by = $lby, locked_at = $lat, deleted_at = $dat
            """,
            ("$id", node.Id), ("$parent", node.ParentId), ("$pk", node.ParentId ?? string.Empty),
            ("$name", node.Name), ("$nk", Key(node.Name)), ("$kind", (int)node.Kind), ("$deleted", node.Deleted ? 1 : 0),
            ("$rev", node.Revision), ("$size", node.Size), ("$mtime", FormatTime(node.MTime)),
            ("$hash", node.Hash?.Value), ("$blocks", string.Join(',', node.Blocks.Select(b => b.Value))),
            ("$lby", node.LockedBy), ("$lat", node.LockedAt is { } la ? FormatTime(la) : null),
            ("$dat", node.DeletedAt is { } da ? FormatTime(da) : null));

        public Task RemoveAsync(string nodeId) =>
            ExecuteAsync("DELETE FROM node WHERE space_id = $s AND id = $id", ("$id", nodeId));

        public async Task<long> SumLiveFileSizesAsync()
        {
            await using var command = Prepare(
                "SELECT COALESCE(SUM(size), 0) FROM node WHERE space_id = $s AND deleted = 0 AND kind = $kind", ("$kind", (int)NodeKind.File));
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<long> AllocateRevisionAsync()
        {
            await using var command = Prepare(
                "UPDATE space SET last_revision = last_revision + 1 WHERE id = $s RETURNING last_revision");
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public Task AppendChangeAsync(ChangeRecord c) => ExecuteAsync("""
            INSERT INTO change (space_id, revision, node_id, op, path, old_path, kind, size, mtime, hash)
            VALUES ($s, $rev, $node, $op, $path, $old, $kind, $size, $mtime, $hash)
            """,
            ("$rev", c.Revision), ("$node", c.NodeId), ("$op", (int)c.Op), ("$path", c.Path.Value),
            ("$old", c.OldPath?.Value), ("$kind", (int)c.Kind), ("$size", c.Size),
            ("$mtime", c.MTime is { } t ? FormatTime(t) : null), ("$hash", c.Hash?.Value));

        public async Task<string?> GetIdempotentResponseAsync(string key)
        {
            await using var command = Prepare(
                "SELECT response FROM idempotency WHERE space_id = $s AND key = $k", ("$k", key));
            return await command.ExecuteScalarAsync() as string;
        }

        public Task PutIdempotentResponseAsync(string key, string response) => ExecuteAsync(
            "INSERT OR REPLACE INTO idempotency (space_id, key, response) VALUES ($s, $k, $r)",
            ("$k", key), ("$r", response));

        public Task<IReadOnlyList<NodeRecord>> ListDeletedAsync() => QueryNodesAsync(
            $"SELECT {NodeColumns} FROM node WHERE space_id = $s AND deleted = 1");

        public Task<IReadOnlyList<NodeRecord>> ListDeletedChildrenAsync(string parentId, long revision) => QueryNodesAsync(
            $"SELECT {NodeColumns} FROM node WHERE space_id = $s AND parent_key = $p AND deleted = 1 AND revision = $rev",
            ("$p", parentId), ("$rev", revision));

        private Task InsertVersionAsync(NodeVersion version) => ExecuteAsync("""
            INSERT OR REPLACE INTO version (space_id, node_id, revision, size, mtime, hash, blocks, created_at, created_by)
            VALUES ($s, $n, $rev, $size, $mtime, $hash, $blocks, $at, $by)
            """,
            ("$n", version.NodeId), ("$rev", version.Revision), ("$size", version.Size), ("$mtime", FormatTime(version.MTime)),
            ("$hash", version.Hash?.Value), ("$blocks", string.Join(',', version.Blocks.Select(b => b.Value))),
            ("$at", FormatTime(version.CreatedAt)), ("$by", version.CreatedBy));

        public async Task<IReadOnlyList<NodeVersion>> ListVersionsAsync(string nodeId)
        {
            await using var command = Prepare("""
                SELECT node_id, revision, size, mtime, hash, blocks, created_at, created_by
                FROM version WHERE space_id = $s AND node_id = $n ORDER BY revision DESC
                """, ("$n", nodeId));
            await using var r = await command.ExecuteReaderAsync();
            var versions = new List<NodeVersion>();
            while (await r.ReadAsync())
            {
                versions.Add(new NodeVersion(
                    r.GetString(0), r.GetInt64(1), r.GetInt64(2), ParseTime(r.GetString(3)),
                    r.IsDBNull(4) ? null : ContentHash.Parse(r.GetString(4)), Blocks(r.GetString(5)),
                    ParseTime(r.GetString(6)), r.IsDBNull(7) ? null : r.GetString(7)));
            }

            return versions;
        }

        public Task RemoveVersionAsync(string nodeId, long revision) => ExecuteAsync(
            "DELETE FROM version WHERE space_id = $s AND node_id = $n AND revision = $rev", ("$n", nodeId), ("$rev", revision));

        private async Task<IReadOnlyList<NodeRecord>> QueryNodesAsync(string sql, params (string Name, object? Value)[] args)
        {
            await using var command = Prepare(sql, args);
            await using var reader = await command.ExecuteReaderAsync();
            var nodes = new List<NodeRecord>();
            while (await reader.ReadAsync())
            {
                nodes.Add(ReadNode(reader));
            }

            return nodes;
        }

        private SqliteCommand Prepare(string sql, params (string Name, object? Value)[] args)
        {
            var command = Command(connection, sql, [("$s", spaceId), .. args]);
            command.Transaction = transaction;
            return command;
        }

        private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] args)
        {
            await using var command = Prepare(sql, args);
            await command.ExecuteNonQueryAsync();
        }

        private async Task<NodeRecord?> QueryNodeAsync(string sql, params (string Name, object? Value)[] args)
        {
            await using var command = Prepare(sql, args);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadNode(reader) : null;
        }
    }
}
