using SyncDrive.Infra.Blob;
using SyncDrive.Server.App.Ports;
using SyncDrive.Server.App.Tests.Fakes;

namespace SyncDrive.Server.App.Tests;

public sealed class InMemorySpaceServiceTests() : SpaceServiceContract(new InMemoryFixture());

public sealed class SqliteSpaceServiceTests() : SpaceServiceContract(new SqliteFixture());

public sealed class FileBlockStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncdrive-blocks-" + Guid.NewGuid().ToString("N"));
    private readonly FileBlockStore _store;

    public FileBlockStoreTests() => _store = new FileBlockStore(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Written_block_can_be_read_back_and_reports_its_size()
    {
        byte[] content = [1, 2, 3, 4];
        var hash = Fixtures.HashOf(content);

        Assert.Equal(BlockWriteResult.Stored, await _store.WriteAsync(hash, new MemoryStream(content), Ct));

        Assert.Equal(4, await _store.GetSizeAsync(hash, Ct));
        await using var stream = (await _store.OpenReadAsync(hash, Ct))!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public async Task Mismatching_content_is_rejected_and_leaves_no_trace()
    {
        var hash = Fixtures.HashOf([1]);

        Assert.Equal(BlockWriteResult.HashMismatch, await _store.WriteAsync(hash, new MemoryStream([2]), Ct));

        Assert.Null(await _store.GetSizeAsync(hash, Ct));
        Assert.Null(await _store.OpenReadAsync(hash, Ct));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".tmp")));
    }

    [Fact]
    public async Task Writing_the_same_block_twice_is_harmless()
    {
        byte[] content = [7, 7];
        var hash = Fixtures.HashOf(content);

        await _store.WriteAsync(hash, new MemoryStream(content), Ct);

        Assert.Equal(BlockWriteResult.Stored, await _store.WriteAsync(hash, new MemoryStream(content), Ct));
    }

    [Fact]
    public async Task Concurrent_writers_of_one_block_all_succeed()
    {
        var content = new byte[300_000];
        Random.Shared.NextBytes(content);
        var hash = Fixtures.HashOf(content);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _store.WriteAsync(hash, new MemoryStream(content), Ct))));

        Assert.All(results, r => Assert.Equal(BlockWriteResult.Stored, r));
        Assert.Equal(content.Length, await _store.GetSizeAsync(hash, Ct));
    }

    [Fact]
    public async Task Blocks_are_fanned_out_into_two_directory_levels()
    {
        byte[] content = [5];
        var hash = Fixtures.HashOf(content);
        await _store.WriteAsync(hash, new MemoryStream(content), Ct);

        var hex = hash.Value["sha256:".Length..];

        Assert.True(File.Exists(Path.Combine(_root, hex[..2], hex[2..4], hex)));
    }
}

public sealed class SqliteSpaceStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "syncdrive-sqlite-" + Guid.NewGuid().ToString("N"));

    public SqliteSpaceStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // gepoolte Verbindungen halten die Datei offen
        Directory.Delete(_directory, recursive: true);
    }

    private string DbPath => Path.Combine(_directory, "server.db");

    private static SyncDrive.Server.App.Spaces.SpaceService ServiceOver(SyncDrive.Infra.Sqlite.SqliteSpaceStore store) =>
        new(store, new FileBlockStore(Path.Combine(Path.GetTempPath(), "unused-blocks-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public async Task An_existing_database_gets_the_new_user_columns_and_keeps_its_users()
    {
        await using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            await old.OpenAsync();
            await using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE user (id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL,
                    is_admin INTEGER NOT NULL, disabled INTEGER NOT NULL);
                INSERT INTO user VALUES ('u_alt', 'Alt', 'ALT', 'hash', 1, 0);
                """;
            await create.ExecuteNonQueryAsync();
        }

        var store = new SyncDrive.Infra.Sqlite.SqliteAccountStore(DbPath);
        var before = await store.FindUserByNameAsync("alt", Ct);
        await store.UpdateUserAsync(before! with { QuotaBytes = 5000 }, Ct);
        var after = await store.FindUserByIdAsync("u_alt", Ct);

        Assert.Equal(("Alt", true, (long?)null, (DateTimeOffset?)null), (before.Name, before.IsAdmin, before.QuotaBytes, before.CreatedAt));
        Assert.Equal(5000, after!.QuotaBytes);
    }

    [Fact]
    public async Task Block_references_of_an_existing_database_are_filled_in_once()
    {
        var blocks = new FileBlockStore(Path.Combine(_directory, "blocks"));
        var service = new SyncDrive.Server.App.Spaces.SpaceService(new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath), blocks);
        var content = new byte[] { 1, 2, 3 };
        var hash = Fakes.Fixtures.HashOf(content);
        await blocks.WriteAsync(hash, new MemoryStream(content), Ct);
        var store = new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath);
        await store.CreateSpaceAsync("s", Ct);
        await service.CommitAsync("s", new SyncDrive.Contracts.CommitRequest(
            SyncDrive.Contracts.CommitOps.Put, "a.bin", 0, Blocks: [hash.Value], Size: 3, MTime: T0, FileHash: hash.Value), null, Ct);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var wipe = connection.CreateCommand();
            wipe.CommandText = "DELETE FROM block_ref; DELETE FROM applied_migration;"; // Stand vor 0.15.0
            await wipe.ExecuteNonQueryAsync();
        }

        var reopened = new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath);
        var found = await reopened.InTransactionAsync("s", tx => tx.FindReferencedBlocksAsync([hash]), Ct);

        Assert.Equal([hash], found);
    }

    [Fact]
    public async Task File_locks_of_an_existing_database_are_removed_on_start()
    {
        var service = ServiceOver(new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath));
        await new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath).CreateSpaceAsync("s", Ct);
        await service.CommitAsync("s", new SyncDrive.Contracts.CommitRequest(SyncDrive.Contracts.CommitOps.Mkdir, "Ordner", 0, MTime: T0), null, Ct);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var old = connection.CreateCommand();
            old.CommandText = """
                ALTER TABLE node ADD COLUMN locked_by TEXT;
                ALTER TABLE node ADD COLUMN locked_at TEXT;
                UPDATE node SET locked_by = 'u_x', locked_at = '2026-10-19T12:00:00.0000000+00:00';
                UPDATE space SET last_revision = 2 WHERE id = 's';
                INSERT INTO change (space_id, revision, node_id, op, path, old_path, kind, size, mtime, hash)
                    SELECT 's', 2, node_id, 3, path, NULL, kind, size, mtime, hash FROM change WHERE space_id = 's' AND revision = 1;
                INSERT INTO notification VALUES ('nt_1', 'u_x', 'unlock_request', '2026-10-19T12:00:00Z', 'u_y', 's', NULL, 'Ordner', 0);
                INSERT INTO notification VALUES ('nt_2', 'u_x', 'shared', '2026-10-19T12:00:00Z', 'u_y', 's', NULL, NULL, 0);
                """; // Stand vor 0.19.0: Sperrspalten, Feed-Eintrag „lock“, Bitte um Entsperren
            await old.ExecuteNonQueryAsync();
        }

        var reopened = ServiceOver(new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath));
        var feed = (await reopened.GetChangesAsync("s", 0, 10, Ct)).Value!.Changes;
        await using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False");
        await check.OpenAsync();
        await using var query = check.CreateCommand();
        query.CommandText = """
            SELECT (SELECT COUNT(*) FROM pragma_table_info('node') WHERE name LIKE 'locked%') || ',' ||
                   (SELECT group_concat(type) FROM notification)
            """;

        Assert.Equal([(1L, "upsert"), (2L, "upsert")], feed.Select(c => (c.Revision, c.Op)));
        Assert.Equal("0,shared", (string?)await query.ExecuteScalarAsync());
        Assert.True((await reopened.GetNodeAsync("s", "Ordner", Ct)).IsSuccess);
    }

    [Fact]
    public async Task State_survives_reopening_the_database()
    {
        var first = new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath);
        await first.CreateSpaceAsync("s", Ct);
        var committed = await ServiceOver(first).CommitAsync(
            "s", new SyncDrive.Contracts.CommitRequest(SyncDrive.Contracts.CommitOps.Mkdir, "a", 0, MTime: T0), null, Ct);

        var second = ServiceOver(new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath));
        var feed = await second.GetChangesAsync("s", 0, 10, Ct);

        Assert.Equal(committed.Value!.NodeId, feed.Value!.Changes[0].NodeId);
    }

    [Fact]
    public async Task Parallel_commits_get_gapless_unique_revisions()
    {
        var store = new SyncDrive.Infra.Sqlite.SqliteSpaceStore(DbPath);
        await store.CreateSpaceAsync("s", Ct);
        var service = ServiceOver(store);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => service.CommitAsync(
            "s", new SyncDrive.Contracts.CommitRequest(SyncDrive.Contracts.CommitOps.Mkdir, $"d{i}", 0, MTime: T0), null, Ct))));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), results.Select(r => r.Value!.Revision).Order());
    }
}
