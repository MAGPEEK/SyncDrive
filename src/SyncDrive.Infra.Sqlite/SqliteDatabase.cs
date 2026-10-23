using Microsoft.Data.Sqlite;

namespace SyncDrive.Infra.Sqlite;

/// <summary>Gemeinsames Schema der Server-Datenbank; jeder Store darf es idempotent anlegen.</summary>
internal static class SqliteDatabase
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS space (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL DEFAULT '',
            last_revision INTEGER NOT NULL DEFAULT 0,
            feed_floor INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS node (
            space_id TEXT NOT NULL,
            id TEXT NOT NULL,
            parent_id TEXT,
            parent_key TEXT NOT NULL,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            kind INTEGER NOT NULL,
            deleted INTEGER NOT NULL,
            revision INTEGER NOT NULL,
            size INTEGER NOT NULL,
            mtime TEXT NOT NULL,
            hash TEXT,
            blocks TEXT NOT NULL,
            deleted_at TEXT,
            PRIMARY KEY (space_id, id));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_node_name ON node (space_id, parent_key, name_key);
        CREATE TABLE IF NOT EXISTS change (
            space_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            node_id TEXT NOT NULL,
            op INTEGER NOT NULL,
            path TEXT NOT NULL,
            old_path TEXT,
            kind INTEGER NOT NULL,
            size INTEGER NOT NULL,
            mtime TEXT,
            hash TEXT,
            PRIMARY KEY (space_id, revision));
        CREATE TABLE IF NOT EXISTS version (
            space_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            size INTEGER NOT NULL,
            mtime TEXT NOT NULL,
            hash TEXT,
            blocks TEXT NOT NULL,
            created_at TEXT NOT NULL,
            created_by TEXT,
            PRIMARY KEY (space_id, node_id, revision));
        CREATE TABLE IF NOT EXISTS idempotency (
            space_id TEXT NOT NULL,
            key TEXT NOT NULL,
            response TEXT NOT NULL,
            PRIMARY KEY (space_id, key));
        CREATE TABLE IF NOT EXISTS user (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL UNIQUE,
            password_hash TEXT NOT NULL,
            is_admin INTEGER NOT NULL,
            disabled INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS device (
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL,
            name TEXT NOT NULL,
            token_hash TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            revoked INTEGER NOT NULL,
            client_type TEXT,
            client_version TEXT,
            last_seen TEXT);
        CREATE TABLE IF NOT EXISTS space_member (
            space_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            role INTEGER NOT NULL,
            PRIMARY KEY (space_id, user_id));
        CREATE TABLE IF NOT EXISTS star (
            user_id TEXT NOT NULL,
            space_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            PRIMARY KEY (user_id, space_id, node_id));
        CREATE TABLE IF NOT EXISTS label (
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL,
            name TEXT NOT NULL,
            color TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS label_item (
            label_id TEXT NOT NULL,
            space_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            PRIMARY KEY (label_id, space_id, node_id));
        CREATE TABLE IF NOT EXISTS notification (
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL,
            type TEXT NOT NULL,
            created_at TEXT NOT NULL,
            actor_user_id TEXT,
            space_id TEXT,
            node_id TEXT,
            path TEXT,
            read INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS node_share (
            id TEXT PRIMARY KEY,
            space_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            role INTEGER NOT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            UNIQUE (space_id, node_id, user_id));
        CREATE INDEX IF NOT EXISTS ix_node_share_user ON node_share (user_id);
        CREATE TABLE IF NOT EXISTS share_link (
            id TEXT PRIMARY KEY,
            token TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            space_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT,
            password_hash TEXT,
            UNIQUE (space_id, node_id, kind));
        CREATE TABLE IF NOT EXISTS user_group (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS group_member (
            group_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            PRIMARY KEY (group_id, user_id));
        CREATE INDEX IF NOT EXISTS ix_group_member_user ON group_member (user_id);
        CREATE TABLE IF NOT EXISTS block_ref (
            space_id TEXT NOT NULL,
            hash TEXT NOT NULL,
            PRIMARY KEY (space_id, hash)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS server_setting (
            name TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS applied_migration (
            name TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS user_external (
            provider TEXT NOT NULL,
            subject TEXT NOT NULL,
            user_id TEXT NOT NULL,
            PRIMARY KEY (provider, subject),
            UNIQUE (provider, user_id));
        CREATE TABLE IF NOT EXISTS space_group (
            space_id TEXT NOT NULL,
            group_id TEXT NOT NULL,
            role INTEGER NOT NULL,
            PRIMARY KEY (space_id, group_id));
        """;

    /// <summary>
    /// Legt Datei und Schema an (WAL) und liefert den Verbindungsstring. Mit Pooling, sonst öffnet jede Abfrage die Datei neu. Wer die Datei
    /// löschen will (Tests), ruft vorher <c>SqliteConnection.ClearAllPools()</c>.
    /// </summary>
    public static string Initialize(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Run(connection, "PRAGMA journal_mode = WAL;");
        lock (MigrationGate)
        {
            Run(connection, Schema);
            AddColumnIfMissing(connection, "user", "quota_bytes", "INTEGER");
            AddColumnIfMissing(connection, "user", "created_at", "TEXT");
            AddColumnIfMissing(connection, "space", "kind", "TEXT NOT NULL DEFAULT 'personal'");
            RemoveFileLocks(connection);
        }

        return connectionString;
    }

    private static readonly Lock MigrationGate = new();

    /// <summary>Führt einen Nachtrag an bestehenden Daten genau einmal je Datenbank aus (in einer Transaktion mit der Markierung).</summary>
    public static void RunOnce(string connectionString, string name, Action<SqliteConnection, SqliteTransaction> migrate)
    {
        lock (MigrationGate)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "INSERT OR IGNORE INTO applied_migration (name) VALUES ($n)";
            mark.Parameters.AddWithValue("$n", name);
            if (mark.ExecuteNonQuery() == 0)
            {
                return; // schon erledigt
            }

            migrate(connection, transaction);
            transaction.Commit();
        }
    }

    /// <summary>Spalten, die nach der ersten Fassung dazukamen; bestehende Datenbanken bekommen sie beim Start (M7: Kontingent, Anlagedatum).</summary>
    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string type)
    {
        if (!HasColumn(connection, table, column))
        {
            Run(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type};");
        }
    }

    /// <summary>
    /// Dateisperren gibt es seit 0.19.0 nicht mehr: Feed-Einträge der Sperre (op 3) werden zu Aktualisierungen (op 0, Clients behandeln sie
    /// als unveränderte Datei), Bitten um Entsperren verschwinden, die Sperrspalten fallen weg.
    /// </summary>
    private static void RemoveFileLocks(SqliteConnection connection)
    {
        if (HasColumn(connection, "node", "locked_by"))
        {
            Run(connection, """
                BEGIN;
                UPDATE change SET op = 0 WHERE op = 3;
                DELETE FROM notification WHERE type = 'unlock_request';
                ALTER TABLE node DROP COLUMN locked_by;
                ALTER TABLE node DROP COLUMN locked_at;
                COMMIT;
                """);
        }
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var info = connection.CreateCommand();
        info.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return (long)info.ExecuteScalar()! > 0;
    }

    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var pragma = Command(connection, "PRAGMA busy_timeout = 5000;");
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

    public static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var connection = await OpenAsync(connectionString, ct);
        await using var command = Command(connection, sql, args);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
