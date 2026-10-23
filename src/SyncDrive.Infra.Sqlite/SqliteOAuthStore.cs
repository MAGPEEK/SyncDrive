using System.Text.Json;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Infra.Sqlite;

/// <summary>
/// Anbindung an den Identitätsanbieter als JSON in <c>server_setting</c> (das Client-Secret liegt wie die übrigen Daten im Volume
/// <c>/data</c>), Verknüpfungen in <c>user_external</c>: je Anbieter ein <c>sub</c> pro Konto und ein Konto pro <c>sub</c>.
/// </summary>
public sealed class SqliteOAuthStore(string databasePath) : IOAuthStore
{
    private const string Name = "oauth";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly string _cs = SqliteDatabase.Initialize(databasePath);

    public async Task<OAuthSettings?> LoadAsync(CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, "SELECT value FROM server_setting WHERE name = $n", ("$n", Name));
        return await command.ExecuteScalarAsync(ct) is string text ? JsonSerializer.Deserialize<OAuthSettings>(text, Json) : null;
    }

    public Task SaveAsync(OAuthSettings settings, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs,
        "INSERT OR REPLACE INTO server_setting (name, value) VALUES ($n, $v)", ct, ("$n", Name), ("$v", JsonSerializer.Serialize(settings, Json)));

    public Task<string?> FindLinkedUserAsync(string provider, string subject, CancellationToken ct) => ScalarAsync(
        "SELECT user_id FROM user_external WHERE provider = $p AND subject = $s", ct, ("$p", provider), ("$s", subject));

    public Task<string?> FindSubjectAsync(string provider, string userId, CancellationToken ct) => ScalarAsync(
        "SELECT subject FROM user_external WHERE provider = $p AND user_id = $u", ct, ("$p", provider), ("$u", userId));

    public Task LinkAsync(string provider, string subject, string userId, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs,
        "INSERT INTO user_external (provider, subject, user_id) VALUES ($p, $s, $u)", ct, ("$p", provider), ("$s", subject), ("$u", userId));

    private async Task<string?> ScalarAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, sql, args);
        return await command.ExecuteScalarAsync(ct) as string;
    }
}
