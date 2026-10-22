using System.Globalization;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Sharing;

namespace SyncDrive.Infra.Sqlite;

/// <summary>Einladungen und öffentliche Links (M6b) in der Server-Datenbank.</summary>
public sealed class SqliteShareStore(string databasePath) : IShareStore
{
    private const string ShareColumns = "id, space_id, node_id, user_id, role, created_by, created_at";
    private const string LinkColumns = "id, token, kind, space_id, node_id, created_by, created_at, expires_at, password_hash";
    private readonly string _cs = SqliteDatabase.Initialize(databasePath);

    public async Task<ShareRecord> SaveShareAsync(ShareRecord share, CancellationToken ct)
    {
        await SqliteDatabase.ExecuteAsync(_cs, $"""
            INSERT INTO node_share ({ShareColumns}) VALUES ($id, $s, $n, $u, $r, $by, $at)
            ON CONFLICT (space_id, node_id, user_id) DO UPDATE SET role = excluded.role
            """, ct, ("$id", share.Id), ("$s", share.SpaceId), ("$n", share.NodeId), ("$u", share.UserId), ("$r", (int)share.Role),
            ("$by", share.CreatedBy), ("$at", Time(share.CreatedAt)));
        var stored = await SharesAsync(
            $"SELECT {ShareColumns} FROM node_share WHERE space_id = $a AND node_id = $b AND user_id = $c", ct,
            ("$a", share.SpaceId), ("$b", share.NodeId), ("$c", share.UserId));
        return stored[0];
    }

    public async Task<ShareRecord?> FindShareAsync(string shareId, CancellationToken ct) =>
        First(await SharesAsync($"SELECT {ShareColumns} FROM node_share WHERE id = $a", ct, ("$a", shareId)));

    public Task<IReadOnlyList<ShareRecord>> ListSharesForUserAsync(string userId, CancellationToken ct) =>
        SharesAsync($"SELECT {ShareColumns} FROM node_share WHERE user_id = $a ORDER BY created_at, id", ct, ("$a", userId));

    public Task<IReadOnlyList<ShareRecord>> ListSharesOfNodeAsync(string spaceId, string nodeId, CancellationToken ct) =>
        SharesAsync($"SELECT {ShareColumns} FROM node_share WHERE space_id = $a AND node_id = $b ORDER BY created_at, id", ct,
            ("$a", spaceId), ("$b", nodeId));

    public Task<IReadOnlyList<ShareRecord>> ListSharesCreatedByAsync(string userId, CancellationToken ct) =>
        SharesAsync($"SELECT {ShareColumns} FROM node_share WHERE created_by = $a ORDER BY created_at, id", ct, ("$a", userId));

    public Task<bool> DeleteShareAsync(string shareId, CancellationToken ct) => DeleteAsync("DELETE FROM node_share WHERE id = $id", shareId, ct);

    public Task SaveLinkAsync(LinkRecord link, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs, $"""
        INSERT INTO share_link ({LinkColumns}) VALUES ($id, $t, $k, $s, $n, $by, $at, $exp, $pw)
        ON CONFLICT (space_id, node_id, kind) DO UPDATE SET expires_at = excluded.expires_at, password_hash = excluded.password_hash
        """, ct, ("$id", link.Id), ("$t", link.Token), ("$k", link.Kind), ("$s", link.SpaceId), ("$n", link.NodeId), ("$by", link.CreatedBy),
        ("$at", Time(link.CreatedAt)), ("$exp", link.ExpiresAt is { } e ? Time(e) : null), ("$pw", link.PasswordHash));

    public async Task<LinkRecord?> FindLinkAsync(string linkId, CancellationToken ct) =>
        First(await LinksAsync($"SELECT {LinkColumns} FROM share_link WHERE id = $a", ct, ("$a", linkId)));

    public async Task<LinkRecord?> FindLinkByTokenAsync(string token, CancellationToken ct) =>
        First(await LinksAsync($"SELECT {LinkColumns} FROM share_link WHERE token = $a", ct, ("$a", token)));

    public Task<IReadOnlyList<LinkRecord>> ListLinksOfNodeAsync(string spaceId, string nodeId, CancellationToken ct) =>
        LinksAsync($"SELECT {LinkColumns} FROM share_link WHERE space_id = $a AND node_id = $b ORDER BY created_at, id", ct,
            ("$a", spaceId), ("$b", nodeId));

    public Task<IReadOnlyList<LinkRecord>> ListLinksCreatedByAsync(string userId, CancellationToken ct) =>
        LinksAsync($"SELECT {LinkColumns} FROM share_link WHERE created_by = $a ORDER BY created_at, id", ct, ("$a", userId));

    public Task<bool> DeleteLinkAsync(string linkId, CancellationToken ct) => DeleteAsync("DELETE FROM share_link WHERE id = $id", linkId, ct);

    private async Task<IReadOnlyList<ShareRecord>> SharesAsync(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, sql, args);
        await using var r = await command.ExecuteReaderAsync(ct);
        var result = new List<ShareRecord>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new ShareRecord(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), (SpaceRole)r.GetInt32(4), r.GetString(5), ParseTime(r.GetString(6))));
        }

        return result;
    }

    private async Task<IReadOnlyList<LinkRecord>> LinksAsync(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, sql, args);
        await using var r = await command.ExecuteReaderAsync(ct);
        var result = new List<LinkRecord>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new LinkRecord(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), ParseTime(r.GetString(6)),
                r.IsDBNull(7) ? null : ParseTime(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8)));
        }

        return result;
    }

    private async Task<bool> DeleteAsync(string sql, string id, CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, sql, ("$id", id));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private static T? First<T>(IReadOnlyList<T> rows) where T : class => rows.Count > 0 ? rows[0] : null;

    private static string Time(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
