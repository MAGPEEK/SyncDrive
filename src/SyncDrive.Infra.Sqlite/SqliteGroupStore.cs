using System.Globalization;
using Microsoft.Data.Sqlite;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Infra.Sqlite;

/// <summary>Benutzergruppen (M7) in der Server-Datenbank; die Rechteauflösung über Gruppen steckt in <see cref="SqliteAccountStore"/>.</summary>
public sealed class SqliteGroupStore(string databasePath) : IGroupStore
{
    private readonly string _cs = SqliteDatabase.Initialize(databasePath);

    public async Task<bool> AddGroupAsync(GroupRecord group, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection,
            "INSERT OR IGNORE INTO user_group (id, name, name_key) VALUES ($id, $n, $k)", ("$id", group.Id), ("$n", group.Name), ("$k", Key(group.Name)));
        return await command.ExecuteNonQueryAsync(ct) > 0; // false: Name schon vergeben
    }

    public async Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, "SELECT id, name FROM user_group ORDER BY name_key");
        await using var r = await command.ExecuteReaderAsync(ct);
        var groups = new List<GroupRecord>();
        while (await r.ReadAsync(ct))
        {
            groups.Add(new GroupRecord(r.GetString(0), r.GetString(1)));
        }

        return groups;
    }

    public async Task<GroupRecord?> FindGroupAsync(string groupId, CancellationToken ct) =>
        (await ListGroupsAsync(ct)).FirstOrDefault(g => g.Id == groupId);

    public async Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var removed = await RunAsync("DELETE FROM user_group WHERE id = $g") > 0;
        await RunAsync("DELETE FROM group_member WHERE group_id = $g");
        await RunAsync("DELETE FROM space_group WHERE group_id = $g");
        await transaction.CommitAsync(ct);
        return removed;

        async Task<int> RunAsync(string sql)
        {
            await using var command = SqliteDatabase.Command(connection, sql, ("$g", groupId));
            command.Transaction = transaction;
            return await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<UserRecord>> ListGroupMembersAsync(string groupId, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, """
            SELECT u.id, u.name, u.password_hash, u.is_admin, u.disabled, u.quota_bytes, u.created_at
            FROM group_member m JOIN user u ON u.id = m.user_id WHERE m.group_id = $g ORDER BY u.name_key
            """, ("$g", groupId));
        await using var r = await command.ExecuteReaderAsync(ct);
        var users = new List<UserRecord>();
        while (await r.ReadAsync(ct))
        {
            users.Add(new UserRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetInt32(4) != 0,
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return users;
    }

    public Task SetGroupMemberAsync(string groupId, string userId, bool member, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs,
        member
            ? "INSERT OR IGNORE INTO group_member (group_id, user_id) VALUES ($g, $u)"
            : "DELETE FROM group_member WHERE group_id = $g AND user_id = $u",
        ct, ("$g", groupId), ("$u", userId));

    private static string Key(string name) => name.Trim().ToUpperInvariant();
}
