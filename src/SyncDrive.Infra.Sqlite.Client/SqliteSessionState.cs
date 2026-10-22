using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SyncDrive.Client.App.Ports;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Infra.Sqlite.Client;

/// <summary>Schema der Client-Datenbank einer Sync-Aufgabe (<c>session-&lt;id&gt;.db</c>).</summary>
internal static class SqliteClientDatabase
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS synced_node (
            path_key TEXT PRIMARY KEY,
            path TEXT NOT NULL,
            node_id TEXT NOT NULL,
            kind INTEGER NOT NULL,
            size INTEGER NOT NULL,
            mtime TEXT NOT NULL,
            hash TEXT,
            remote_revision INTEGER NOT NULL,
            synced_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS setting (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS activity (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            at TEXT NOT NULL,
            action INTEGER NOT NULL,
            path TEXT NOT NULL,
            is_dir INTEGER NOT NULL,
            result INTEGER NOT NULL,
            reason TEXT);
        """;

    public static string Initialize(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;" + Schema;
        command.ExecuteNonQuery();
        return connectionString;
    }

    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static string Time(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Schlüssel ohne Beachtung der Schreibweise, wie der Rest des Systems (Windows-Semantik).</summary>
    public static string Key(RelativePath path) => path.Value.ToUpperInvariant();
}

/// <summary>Basis des Dreiwege-Vergleichs und Feed-Cursor einer Sync-Aufgabe in SQLite.</summary>
public sealed class SqliteSessionState(string databasePath) : ISessionState
{
    private const string CursorKey = "feed_cursor";
    private const string Columns = "path, node_id, kind, size, mtime, hash, remote_revision, synced_at";

    private readonly string _connectionString = SqliteClientDatabase.Initialize(databasePath);

    public async Task<SyncedNode?> GetAsync(RelativePath path, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection,
            $"SELECT {Columns} FROM synced_node WHERE path_key = $k", ("$k", SqliteClientDatabase.Key(path)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task SaveAsync(SyncedNode node, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection, $"""
            INSERT OR REPLACE INTO synced_node (path_key, {Columns})
            VALUES ($k, $p, $n, $kind, $size, $mtime, $hash, $rev, $at)
            """,
            ("$k", SqliteClientDatabase.Key(node.Path)), ("$p", node.Path.Value), ("$n", node.NodeId),
            ("$kind", (int)node.State.Kind), ("$size", node.State.Size), ("$mtime", SqliteClientDatabase.Time(node.State.MTime)),
            ("$hash", node.State.Hash?.Value), ("$rev", node.RemoteRevision), ("$at", SqliteClientDatabase.Time(node.SyncedAt)));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveSubtreeAsync(RelativePath path, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        var key = SqliteClientDatabase.Key(path);
        await using var command = path.IsRoot
            ? SqliteClientDatabase.Command(connection, "DELETE FROM synced_node")
            : SqliteClientDatabase.Command(connection,
                "DELETE FROM synced_node WHERE path_key = $k OR substr(path_key, 1, length($prefix)) = $prefix",
                ("$k", key), ("$prefix", key + "/"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async IAsyncEnumerable<SyncedNode> ListAsync(RelativePath root, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = root.IsRoot
            ? SqliteClientDatabase.Command(connection, $"SELECT {Columns} FROM synced_node ORDER BY path")
            : SqliteClientDatabase.Command(connection,
                $"SELECT {Columns} FROM synced_node WHERE substr(path_key, 1, length($prefix)) = $prefix ORDER BY path",
                ("$prefix", SqliteClientDatabase.Key(root) + "/"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return Read(reader);
        }
    }

    public async Task<long> GetFeedCursorAsync(CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection, "SELECT value FROM setting WHERE key = $k", ("$k", CursorKey));
        return await command.ExecuteScalarAsync(ct) is string text ? long.Parse(text, CultureInfo.InvariantCulture) : 0;
    }

    public async Task SetFeedCursorAsync(long revision, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection,
            "INSERT OR REPLACE INTO setting (key, value) VALUES ($k, $v)",
            ("$k", CursorKey), ("$v", revision.ToString(CultureInfo.InvariantCulture)));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static SyncedNode Read(SqliteDataReader r)
    {
        var kind = (NodeKind)r.GetInt32(2);
        var mtime = SqliteClientDatabase.Time(r.GetString(4));
        var hash = r.IsDBNull(5) ? (ContentHash?)null : ContentHash.Parse(r.GetString(5));
        var state = kind == NodeKind.Directory ? NodeState.Directory(mtime) : NodeState.File(r.GetInt64(3), mtime, hash);
        return new SyncedNode(RelativePath.Parse(r.GetString(0)), r.GetString(1), state, r.GetInt64(6), SqliteClientDatabase.Time(r.GetString(7)));
    }
}

/// <summary>Verlauf in SQLite; behält die neuesten <see cref="MaxEntries"/> Einträge.</summary>
public sealed class SqliteActivityLog(string databasePath) : IActivityLog
{
    public const int MaxEntries = 10_000;
    private const int TrimEvery = 500;

    private readonly string _connectionString = SqliteClientDatabase.Initialize(databasePath);
    private int _sinceTrim;

    public async Task AppendAsync(ActivityEntry entry, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using (var insert = SqliteClientDatabase.Command(connection,
            "INSERT INTO activity (at, action, path, is_dir, result, reason) VALUES ($at, $a, $p, $d, $r, $why)",
            ("$at", SqliteClientDatabase.Time(entry.At)), ("$a", (int)entry.Action), ("$p", entry.Path.Value),
            ("$d", entry.IsDirectory ? 1 : 0), ("$r", (int)entry.Result), ("$why", entry.Reason)))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        if (Interlocked.Increment(ref _sinceTrim) % TrimEvery == 0)
        {
            await using var trim = SqliteClientDatabase.Command(connection,
                "DELETE FROM activity WHERE id <= (SELECT MAX(id) FROM activity) - $max", ("$max", MaxEntries));
            await trim.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<ActivityEntry>> RecentAsync(int limit, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection,
            "SELECT at, action, path, is_dir, result, reason FROM activity ORDER BY id DESC LIMIT $n", ("$n", limit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var entries = new List<ActivityEntry>();
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new ActivityEntry(
                SqliteClientDatabase.Time(reader.GetString(0)), (ActivityAction)reader.GetInt32(1), RelativePath.Parse(reader.GetString(2)),
                reader.GetInt32(3) != 0, (ActivityResult)reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return entries;
    }
}
