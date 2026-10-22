using System.Globalization;
using Microsoft.Data.Sqlite;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Infra.Sqlite;

/// <summary>Favoriten und Labels je Benutzer (B8) in der Server-Datenbank.</summary>
public sealed class SqliteUserItemStore(string databasePath) : IUserItemStore
{
    private readonly string _cs = SqliteDatabase.Initialize(databasePath);

    public Task SetStarAsync(string userId, NodeReference node, bool starred, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs,
        starred
            ? "INSERT OR IGNORE INTO star (user_id, space_id, node_id) VALUES ($u, $s, $n)"
            : "DELETE FROM star WHERE user_id = $u AND space_id = $s AND node_id = $n",
        ct, ("$u", userId), ("$s", node.SpaceId), ("$n", node.NodeId));

    public Task<IReadOnlyList<NodeReference>> ListStarsAsync(string userId, CancellationToken ct) => ReferencesAsync(
        "SELECT space_id, node_id FROM star WHERE user_id = $k ORDER BY rowid", userId, ct);

    public Task SaveLabelAsync(LabelRecord label, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs,
        "INSERT OR REPLACE INTO label (id, user_id, name, color) VALUES ($id, $u, $n, $c)",
        ct, ("$id", label.Id), ("$u", label.UserId), ("$n", label.Name), ("$c", label.Color));

    public async Task<bool> DeleteLabelAsync(string userId, string labelId, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var delete = SqliteDatabase.Command(connection, "DELETE FROM label WHERE id = $id AND user_id = $u", ("$id", labelId), ("$u", userId));
        delete.Transaction = transaction;
        var removed = await delete.ExecuteNonQueryAsync(ct) > 0;
        if (removed)
        {
            await using var items = SqliteDatabase.Command(connection, "DELETE FROM label_item WHERE label_id = $id", ("$id", labelId));
            items.Transaction = transaction;
            await items.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return removed;
    }

    public async Task<IReadOnlyList<LabelRecord>> ListLabelsAsync(string userId, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection,
            "SELECT id, user_id, name, color FROM label WHERE user_id = $u ORDER BY name COLLATE NOCASE, id", ("$u", userId));
        await using var r = await command.ExecuteReaderAsync(ct);
        var labels = new List<LabelRecord>();
        while (await r.ReadAsync(ct))
        {
            labels.Add(new LabelRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        return labels;
    }

    public Task SetLabelAsync(string labelId, NodeReference node, bool assigned, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs,
        assigned
            ? "INSERT OR IGNORE INTO label_item (label_id, space_id, node_id) VALUES ($l, $s, $n)"
            : "DELETE FROM label_item WHERE label_id = $l AND space_id = $s AND node_id = $n",
        ct, ("$l", labelId), ("$s", node.SpaceId), ("$n", node.NodeId));

    public Task<IReadOnlyList<NodeReference>> ListLabeledAsync(string labelId, CancellationToken ct) => ReferencesAsync(
        "SELECT space_id, node_id FROM label_item WHERE label_id = $k ORDER BY rowid", labelId, ct);

    private async Task<IReadOnlyList<NodeReference>> ReferencesAsync(string sql, string key, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, sql, ("$k", key));
        await using var r = await command.ExecuteReaderAsync(ct);
        var refs = new List<NodeReference>();
        while (await r.ReadAsync(ct))
        {
            refs.Add(new NodeReference(r.GetString(0), r.GetString(1)));
        }

        return refs;
    }
}

/// <summary>Benachrichtigungen je Benutzer (Tray-Reiter „Notifications“, Web „Notification Center“).</summary>
public sealed class SqliteNotificationStore(string databasePath) : INotificationStore
{
    private readonly string _cs = SqliteDatabase.Initialize(databasePath);

    public Task AddAsync(NotificationRecord notification, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs, """
        INSERT INTO notification (id, user_id, type, created_at, actor_user_id, space_id, node_id, path, read)
        VALUES ($id, $u, $t, $at, $actor, $s, $n, $p, $r)
        """, ct, ("$id", notification.Id), ("$u", notification.UserId), ("$t", notification.Type), ("$at", notification.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
        ("$actor", notification.ActorUserId), ("$s", notification.SpaceId), ("$n", notification.NodeId), ("$p", notification.Path),
        ("$r", notification.Read ? 1 : 0));

    public async Task<IReadOnlyList<NotificationRecord>> ListAsync(string userId, int limit, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, """
            SELECT id, user_id, type, created_at, actor_user_id, space_id, node_id, path, read
            FROM notification WHERE user_id = $u ORDER BY created_at DESC, rowid DESC LIMIT $n
            """, ("$u", userId), ("$n", limit));
        await using var r = await command.ExecuteReaderAsync(ct);
        var result = new List<NotificationRecord>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new NotificationRecord(
                r.GetString(0), r.GetString(1), r.GetString(2),
                DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt32(8) != 0));
        }

        return result;
    }

    public async Task<bool> MarkReadAsync(string userId, string notificationId, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection,
            "UPDATE notification SET read = 1 WHERE id = $id AND user_id = $u", ("$id", notificationId), ("$u", userId));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }
}
