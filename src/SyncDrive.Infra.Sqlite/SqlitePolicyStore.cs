using System.Text.Json;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Policies;

namespace SyncDrive.Infra.Sqlite;

/// <summary>Richtlinien als JSON in <c>server_setting</c> (eine Zeile je Richtlinienart); neue Felder bekommen beim Lesen ihre Vorgabe.</summary>
public sealed class SqlitePolicyStore(string databasePath) : IPolicyStore
{
    private const string Name = "sharing_policies";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly string _cs = SqliteDatabase.Initialize(databasePath);

    public async Task<SharingPolicies?> LoadAsync(CancellationToken ct)
    {
        await using var connection = await SqliteDatabase.OpenAsync(_cs, ct);
        await using var command = SqliteDatabase.Command(connection, "SELECT value FROM server_setting WHERE name = $n", ("$n", Name));
        return await command.ExecuteScalarAsync(ct) is string text ? JsonSerializer.Deserialize<SharingPolicies>(text, Json) : null;
    }

    public Task SaveAsync(SharingPolicies policies, CancellationToken ct) => SqliteDatabase.ExecuteAsync(_cs,
        "INSERT OR REPLACE INTO server_setting (name, value) VALUES ($n, $v)", ct, ("$n", Name), ("$v", JsonSerializer.Serialize(policies, Json)));
}
