using System.Globalization;
using Microsoft.Data.Sqlite;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Infra.Sqlite;

/// <summary>Benutzer, Geräte und Space-Mitgliedschaften in derselben SQLite-Datenbank wie die Dateibäume.</summary>
public sealed class SqliteAccountStore : IAccountStore
{
    private readonly string _connectionString;

    public SqliteAccountStore(string databasePath) => _connectionString = SqliteDatabase.Initialize(databasePath);

    public async Task<bool> AnyUserAsync(CancellationToken ct) =>
        await ScalarAsync("SELECT 1 FROM user LIMIT 1", ct) is not null;

    public async Task<UserRecord?> FindUserByNameAsync(string name, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            $"SELECT {UserColumns} FROM user WHERE name_key = $k", ("$k", Key(name)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader, 0) : null;
    }

    public Task AddUserAsync(UserRecord user, CancellationToken ct) => ExecuteAsync(
        "INSERT INTO user (id, name, name_key, password_hash, is_admin, disabled, quota_bytes, created_at) VALUES ($id, $n, $k, $p, $a, $d, $q, $c)",
        ct, ("$id", user.Id), ("$n", user.Name), ("$k", Key(user.Name)), ("$p", user.PasswordHash),
        ("$a", user.IsAdmin ? 1 : 0), ("$d", user.Disabled ? 1 : 0), ("$q", user.QuotaBytes), ("$c", user.CreatedAt is { } c ? Time(c) : null));

    public async Task<UserRecord?> FindUserByIdAsync(string userId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            $"SELECT {UserColumns} FROM user WHERE id = $id", ("$id", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader, 0) : null;
    }

    private const string UserColumns = "id, name, password_hash, is_admin, disabled, quota_bytes, created_at";
    private const string UserColumnsU = "u.id, u.name, u.password_hash, u.is_admin, u.disabled, u.quota_bytes, u.created_at";

    public async Task<IReadOnlyList<UserRecord>> ListUsersAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, $"SELECT {UserColumns} FROM user ORDER BY name_key");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var users = new List<UserRecord>();
        while (await reader.ReadAsync(ct))
        {
            users.Add(ReadUser(reader, 0));
        }

        return users;
    }

    public Task UpdateUserAsync(UserRecord user, CancellationToken ct) => ExecuteAsync(
        "UPDATE user SET password_hash = $p, is_admin = $a, disabled = $d, quota_bytes = $q WHERE id = $id",
        ct, ("$id", user.Id), ("$p", user.PasswordHash), ("$a", user.IsAdmin ? 1 : 0), ("$d", user.Disabled ? 1 : 0), ("$q", user.QuotaBytes));

    public Task RevokeAllDevicesAsync(string userId, CancellationToken ct) =>
        ExecuteAsync("UPDATE device SET revoked = 1 WHERE user_id = $u", ct, ("$u", userId));

    public async Task<long?> GetOwnerQuotaAsync(string spaceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT u.quota_bytes FROM space_member m JOIN user u ON u.id = m.user_id
            WHERE m.space_id = $s AND m.role = $owner AND u.quota_bytes IS NOT NULL ORDER BY u.quota_bytes LIMIT 1
            """, ("$s", spaceId), ("$owner", (int)SpaceRole.Owner));
        return await command.ExecuteScalarAsync(ct) is long quota ? quota : null;
    }

    private const string DeviceColumns =
        "d.id, d.user_id, d.name, d.token_hash, d.created_at, d.revoked, d.client_type, d.client_version, d.last_seen";

    public Task AddDeviceAsync(DeviceRecord device, CancellationToken ct) => ExecuteAsync("""
        INSERT INTO device (id, user_id, name, token_hash, created_at, revoked, client_type, client_version, last_seen)
        VALUES ($id, $u, $n, $t, $c, $r, $type, $ver, $seen)
        """,
        ct, ("$id", device.Id), ("$u", device.UserId), ("$n", device.Name), ("$t", device.TokenHash),
        ("$c", Time(device.CreatedAt)), ("$r", device.Revoked ? 1 : 0), ("$type", device.ClientType),
        ("$ver", device.ClientVersion), ("$seen", device.LastSeen is { } seen ? Time(seen) : null));

    public async Task<(DeviceRecord Device, UserRecord User)?> FindDeviceByTokenHashAsync(
        string tokenHash, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, $"""
            SELECT {UserColumnsU}, {DeviceColumns}
            FROM device d JOIN user u ON u.id = d.user_id WHERE d.token_hash = $t
            """, ("$t", tokenHash));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return (ReadDevice(reader, 7), ReadUser(reader, 0));
    }

    public async Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(string userId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            $"SELECT {DeviceColumns} FROM device d WHERE d.user_id = $u AND d.revoked = 0 ORDER BY d.created_at", ("$u", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var devices = new List<DeviceRecord>();
        while (await reader.ReadAsync(ct))
        {
            devices.Add(ReadDevice(reader, 0));
        }

        return devices;
    }

    public Task TouchDeviceAsync(string deviceId, DateTimeOffset seenAt, CancellationToken ct) => ExecuteAsync(
        "UPDATE device SET last_seen = $seen WHERE id = $id", ct, ("$seen", Time(seenAt)), ("$id", deviceId));

    public Task SetMemberAsync(string spaceId, string userId, SpaceRole role, CancellationToken ct) => ExecuteAsync(
        "INSERT OR REPLACE INTO space_member (space_id, user_id, role) VALUES ($s, $u, $r)",
        ct, ("$s", spaceId), ("$u", userId), ("$r", (int)role));

    private static DeviceRecord ReadDevice(SqliteDataReader r, int o) => new(
        r.GetString(o), r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), ParseTime(r.GetString(o + 4)),
        r.GetInt32(o + 5) != 0, r.IsDBNull(o + 6) ? null : r.GetString(o + 6), r.IsDBNull(o + 7) ? null : r.GetString(o + 7),
        r.IsDBNull(o + 8) ? null : ParseTime(r.GetString(o + 8)));

    private static string Time(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public async Task<bool> RevokeDeviceAsync(string deviceId, string userId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            "UPDATE device SET revoked = 1 WHERE id = $id AND user_id = $u", ("$id", deviceId), ("$u", userId));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task CreateSpaceAsync(string spaceId, string name, string ownerUserId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var (sql, args) in new (string, (string, object?)[])[]
        {
            ("INSERT INTO space (id, name) VALUES ($s, $n)", [("$s", spaceId), ("$n", name)]),
            ("INSERT INTO space_member (space_id, user_id, role) VALUES ($s, $u, $r)",
                [("$s", spaceId), ("$u", ownerUserId), ("$r", (int)SpaceRole.Owner)]),
        })
        {
            await using var command = Command(connection, sql, args);
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Rollen aus direkter Mitgliedschaft und aus Gruppen; je Space gilt die höchste.</summary>
    private const string RolesOfUser = """
        SELECT space_id, role FROM space_member WHERE user_id = $u
        UNION ALL
        SELECT sg.space_id, sg.role FROM space_group sg JOIN group_member gm ON gm.group_id = sg.group_id WHERE gm.user_id = $u
        """;

    public async Task<IReadOnlyList<SpaceMembership>> ListMembershipsAsync(string userId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, $"""
            SELECT s.id, s.name, MAX(r.role) FROM ({RolesOfUser}) r JOIN space s ON s.id = r.space_id
            GROUP BY s.id, s.name ORDER BY s.name, s.id
            """, ("$u", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SpaceMembership>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SpaceMembership(reader.GetString(0), reader.GetString(1), (SpaceRole)reader.GetInt32(2)));
        }

        return result;
    }

    public async Task<SpaceRole?> GetRoleAsync(string userId, string spaceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            $"SELECT MAX(role) FROM ({RolesOfUser}) WHERE space_id = $s", ("$s", spaceId), ("$u", userId));
        return await command.ExecuteScalarAsync(ct) is long role ? (SpaceRole)role : null;
    }

    public Task CreateTeamSpaceAsync(string spaceId, string name, CancellationToken ct) => ExecuteAsync(
        "INSERT INTO space (id, name, kind) VALUES ($s, $n, 'team')", ct, ("$s", spaceId), ("$n", name));

    public async Task<IReadOnlyList<(string Id, string Name)>> ListTeamSpacesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, "SELECT id, name FROM space WHERE kind = 'team' ORDER BY name COLLATE NOCASE, id");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<(string, string)>();
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    public Task RenameSpaceAsync(string spaceId, string name, CancellationToken ct) =>
        ExecuteAsync("UPDATE space SET name = $n WHERE id = $s", ct, ("$s", spaceId), ("$n", name));

    public async Task<IReadOnlyList<SpaceMember>> ListSpaceMembersAsync(string spaceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT 'user', u.id, u.name, m.role FROM space_member m JOIN user u ON u.id = m.user_id WHERE m.space_id = $s
            UNION ALL
            SELECT 'group', g.id, g.name, sg.role FROM space_group sg JOIN user_group g ON g.id = sg.group_id WHERE sg.space_id = $s
            ORDER BY 1 DESC, 3 COLLATE NOCASE
            """, ("$s", spaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SpaceMember>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SpaceMember(reader.GetString(0), reader.GetString(1), reader.GetString(2), (SpaceRole)reader.GetInt32(3)));
        }

        return result;
    }

    public Task SetGroupRoleAsync(string spaceId, string groupId, SpaceRole role, CancellationToken ct) => ExecuteAsync(
        "INSERT OR REPLACE INTO space_group (space_id, group_id, role) VALUES ($s, $g, $r)", ct, ("$s", spaceId), ("$g", groupId), ("$r", (int)role));

    public Task<bool> RemoveMemberAsync(string spaceId, string userId, CancellationToken ct) =>
        DeleteAsync("DELETE FROM space_member WHERE space_id = $s AND user_id = $x", spaceId, userId, ct);

    public Task<bool> RemoveGroupAsync(string spaceId, string groupId, CancellationToken ct) =>
        DeleteAsync("DELETE FROM space_group WHERE space_id = $s AND group_id = $x", spaceId, groupId, ct);

    private async Task<bool> DeleteAsync(string sql, string spaceId, string other, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, sql, ("$s", spaceId), ("$x", other));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private static string Key(string name) => name.Trim().ToUpperInvariant();

    private static UserRecord ReadUser(SqliteDataReader r, int offset) => new(
        r.GetString(offset), r.GetString(offset + 1), r.GetString(offset + 2),
        r.GetInt32(offset + 3) != 0, r.GetInt32(offset + 4) != 0,
        r.IsDBNull(offset + 5) ? null : r.GetInt64(offset + 5), r.IsDBNull(offset + 6) ? null : ParseTime(r.GetString(offset + 6)));

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

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var pragma = Command(connection, "PRAGMA busy_timeout = 5000;");
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, sql);
        return await command.ExecuteScalarAsync(ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, sql, args);
        await command.ExecuteNonQueryAsync(ct);
    }
}
