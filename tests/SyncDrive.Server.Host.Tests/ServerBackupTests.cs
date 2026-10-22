using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Tägliche Sicherung (M7): konsistenter Datenbank-Stand im laufenden Betrieb, Blöcke inkrementell, Aufbewahrung, Zeitplan.</summary>
public sealed class ServerBackupTests(HostFactory factory) : IClassFixture<HostFactory>, IDisposable
{
    private readonly string _target = Path.Combine(Path.GetTempPath(), "syncdrive-backup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_target, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private ServerBackup Backup(TimeProvider clock, int keep = 7)
    {
        var running = factory.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        return new ServerBackup(
            Options.Create(new ServerOptions { DataDirectory = running.DataDirectory, BackupDirectory = _target, BackupsToKeep = keep }), clock);
    }

    private static async Task PutAsync(ApiClient api, string space, string path, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        using (var body = new ByteArrayContent(bytes))
        {
            (await api.Http.PutAsync($"/api/v1/blocks/{hash}", body)).EnsureSuccessStatusCode();
        }

        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, path, 0, Blocks: [hash], Size: bytes.Length, MTime: DateTimeOffset.UtcNow, FileHash: hash))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_backup_holds_a_readable_database_state_and_copies_blocks_only_once()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var name = "gesichert-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        await PutAsync(api, space, name, "Inhalt " + name);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 22, 2, 30, 0, TimeSpan.Zero));

        var (snapshot, firstBlocks) = await Backup(clock).RunAsync(default);
        var (_, secondBlocks) = await Backup(clock).RunAsync(default);

        Assert.Equal("server-20261022-023000.db", snapshot);
        Assert.True(firstBlocks >= 1);
        Assert.Equal(0, secondBlocks);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_target, "db", snapshot)};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM node WHERE name = $n AND deleted = 0";
        command.Parameters.AddWithValue("$n", name);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        Assert.Empty(Directory.GetFiles(_target, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Only_the_newest_database_states_are_kept()
    {
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 22, 2, 30, 0, TimeSpan.Zero));

        for (var day = 0; day < 4; day++)
        {
            await Backup(clock, keep: 2).RunAsync(default);
        }

        Assert.Equal(
            ["server-20261024-023000.db", "server-20261025-023000.db"],
            Directory.GetFiles(Path.Combine(_target, "db")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("2026-10-22T01:00:00Z", "2026-10-22T02:30:00Z")]
    [InlineData("2026-10-22T02:30:00Z", "2026-10-23T02:30:00Z")]
    [InlineData("2026-10-22T23:00:00Z", "2026-10-23T02:30:00Z")]
    public void The_next_backup_is_the_coming_occurrence_of_the_time(string now, string expected)
    {
        var next = ServerBackup.NextRun(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), new TimeOnly(2, 30), TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), next);
    }

    [Fact]
    public void The_backup_time_follows_the_local_zone()
    {
        var berlinLike = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");

        var next = ServerBackup.NextRun(new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.Zero), new TimeOnly(2, 30), berlinLike);

        Assert.Equal(new DateTimeOffset(2026, 10, 23, 2, 30, 0, TimeSpan.FromHours(2)), next); // 03:00 Ortszeit ist schon nach 02:30
    }

    /// <summary>Jede Abfrage der Uhr rückt einen Tag weiter, damit jeder Lauf einen eigenen Stand-Namen bekommt.</summary>
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start.AddDays(-1);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddDays(1);
    }
}
