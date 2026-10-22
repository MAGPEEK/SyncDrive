using System.Text.Json;
using Microsoft.Data.Sqlite;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;

namespace SyncDrive.Infra.Sqlite.Client;

/// <summary><c>client.db</c>: Verbindungen, Aufgaben, Einstellungen. Aufgaben-Filter und Einstellungen als JSON (wachsen additiv).</summary>
public sealed class SqliteClientConfigStore : IClientConfigStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS connection (
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            server_url TEXT NOT NULL,
            user_name TEXT NOT NULL,
            device_id TEXT NOT NULL,
            pinned_cert TEXT);
        CREATE TABLE IF NOT EXISTS sync_task (
            id TEXT PRIMARY KEY,
            connection_id TEXT NOT NULL,
            space_id TEXT NOT NULL,
            remote_path TEXT NOT NULL,
            local_path TEXT NOT NULL,
            direction INTEGER NOT NULL,
            keep_local_deletes INTEGER NOT NULL,
            on_demand INTEGER NOT NULL,
            advanced_check INTEGER NOT NULL,
            filters TEXT NOT NULL,
            paused INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS app_setting (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        """;

    private const string SettingsKey = "client_settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new RelativePathConverter(), new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _connectionString;

    public SqliteClientConfigStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;" + Schema;
        command.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<ConnectionInfo>> ListConnectionsAsync(CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection,
            "SELECT id, display_name, server_url, user_name, device_id, pinned_cert FROM connection ORDER BY display_name, id");
        await using var r = await command.ExecuteReaderAsync(ct);
        var result = new List<ConnectionInfo>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new ConnectionInfo(r.GetString(0), r.GetString(1), new Uri(r.GetString(2)), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5)));
        }

        return result;
    }

    public async Task SaveConnectionAsync(ConnectionInfo connection, CancellationToken ct)
    {
        await using var db = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(db, """
            INSERT OR REPLACE INTO connection (id, display_name, server_url, user_name, device_id, pinned_cert)
            VALUES ($id, $name, $url, $user, $device, $pin)
            """, ("$id", connection.Id), ("$name", connection.DisplayName), ("$url", connection.ServerUrl.ToString()), ("$user", connection.UserName),
            ("$device", connection.DeviceId), ("$pin", connection.PinnedCertificateSha256));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveConnectionAsync(string connectionId, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var sql in new[] { "DELETE FROM sync_task WHERE connection_id = $id", "DELETE FROM connection WHERE id = $id" })
        {
            await using var command = SqliteClientDatabase.Command(connection, sql, ("$id", connectionId));
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SyncTaskDefinition>> ListTasksAsync(CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection, """
            SELECT id, connection_id, space_id, remote_path, local_path, direction, keep_local_deletes, on_demand, advanced_check, filters, paused
            FROM sync_task ORDER BY local_path, id
            """);
        await using var r = await command.ExecuteReaderAsync(ct);
        var result = new List<SyncTaskDefinition>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new SyncTaskDefinition(
                r.GetString(0), r.GetString(1), r.GetString(2), RelativePath.Parse(r.GetString(3)), r.GetString(4),
                (SyncDirection)r.GetInt32(5), r.GetInt32(6) != 0, r.GetInt32(7) != 0, r.GetInt32(8) != 0,
                JsonSerializer.Deserialize<TaskFilterSettings>(r.GetString(9), Json), r.GetInt32(10) != 0));
        }

        return result;
    }

    public async Task SaveTaskAsync(SyncTaskDefinition task, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection, """
            INSERT OR REPLACE INTO sync_task
                (id, connection_id, space_id, remote_path, local_path, direction, keep_local_deletes, on_demand, advanced_check, filters, paused)
            VALUES ($id, $conn, $space, $remote, $local, $dir, $keep, $od, $adv, $filters, $paused)
            """, ("$id", task.Id), ("$conn", task.ConnectionId), ("$space", task.SpaceId), ("$remote", task.RemotePath.Value), ("$local", task.LocalPath),
            ("$dir", (int)task.Direction), ("$keep", task.KeepLocallyDeletedOnServer ? 1 : 0), ("$od", task.OnDemand ? 1 : 0),
            ("$adv", task.AdvancedConsistencyCheck ? 1 : 0), ("$filters", JsonSerializer.Serialize(task.Filters ?? new TaskFilterSettings(), Json)),
            ("$paused", task.Paused ? 1 : 0));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveTaskAsync(string taskId, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection, "DELETE FROM sync_task WHERE id = $id", ("$id", taskId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ClientSettings> GetSettingsAsync(CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection, "SELECT value FROM app_setting WHERE key = $k", ("$k", SettingsKey));
        return await command.ExecuteScalarAsync(ct) is string json
            ? JsonSerializer.Deserialize<ClientSettings>(json, Json) ?? new ClientSettings()
            : new ClientSettings();
    }

    public async Task SaveSettingsAsync(ClientSettings settings, CancellationToken ct)
    {
        await using var connection = await SqliteClientDatabase.OpenAsync(_connectionString, ct);
        await using var command = SqliteClientDatabase.Command(connection,
            "INSERT OR REPLACE INTO app_setting (key, value) VALUES ($k, $v)", ("$k", SettingsKey), ("$v", JsonSerializer.Serialize(settings, Json)));
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class RelativePathConverter : System.Text.Json.Serialization.JsonConverter<RelativePath>
    {
        public override RelativePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            RelativePath.Parse(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, RelativePath value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
