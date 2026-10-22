using System.Text;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Spaces;
using SyncDrive.Server.App.Tests.Fakes;

namespace SyncDrive.Server.App.Tests;

public abstract partial class SpaceServiceContract : IDisposable
{
    private const string Space = "s1";
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly IStoreFixture _fixture;
    private readonly SpaceService _service;

    protected ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));

    protected SpaceService Service => _service;

    protected static string SpaceId => Space;

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    protected SpaceServiceContract(IStoreFixture fixture)
    {
        _fixture = fixture;
        fixture.AddSpace(Space);
        _service = new SpaceService(fixture.Store, fixture.Blocks, null, Clock, new SpaceServiceOptions(VersionsToKeep: 3));
    }

    private static CommitRequest Mkdir(string path, long baseRev = 0) =>
        new(CommitOps.Mkdir, path, baseRev, MTime: T0);

    private static CommitRequest Delete(string path, long baseRev) => new(CommitOps.Delete, path, baseRev);

    private static CommitRequest Move(string from, string to, long baseRev) =>
        new(CommitOps.Move, from, baseRev, NewPath: to);

    private CommitRequest Put(string path, string content, long baseRev = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = _fixture.AddBlock(bytes).Value;
        return new CommitRequest(
            CommitOps.Put, path, baseRev, Blocks: bytes.Length == 0 ? [] : [hash], Size: bytes.Length,
            MTime: T0, FileHash: hash);
    }

    private async Task<CommitResponse> Ok(CommitRequest request, string? key = null)
    {
        var result = await _service.CommitAsync(Space, request, key, Ct);
        Assert.True(result.IsSuccess, result.Error?.Code + ": " + result.Error?.Message);
        return result.Value!;
    }

    private async Task<ServiceError> Rejected(CommitRequest request)
    {
        var result = await _service.CommitAsync(Space, request, null, Ct);
        Assert.False(result.IsSuccess);
        return result.Error!;
    }

    private async Task<IEnumerable<ChangeEntry>> Feed(long after = 0)
    {
        var result = await _service.GetChangesAsync(Space, after, 100, Ct);
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value!.Changes.AsEnumerable();
    }

    [Fact]
    public async Task Revisions_are_gapless_and_monotonic()
    {
        var a = await Ok(Mkdir("a"));
        var b = await Ok(Put("a/x.txt", "x"));
        var c = await Ok(Mkdir("b"));

        Assert.Equal([1L, 2L, 3L], [a.Revision, b.Revision, c.Revision]);
        Assert.Equal([1L, 2L, 3L], (await Feed()).Select(e => e.Revision));
    }

    [Fact]
    public async Task Put_requires_existing_parent_folder()
    {
        var error = await Rejected(Put("nope/x.txt", "x"));

        Assert.Equal(ErrorCode.ParentMissing, error.Code);
    }

    [Fact]
    public async Task Put_rejects_blocks_the_server_does_not_have()
    {
        var unknown = Fixtures.HashOf([1, 2, 3]).Value;
        var request = new CommitRequest(CommitOps.Put, "x.txt", 0, Blocks: [unknown], Size: 3, MTime: T0, FileHash: unknown);

        var error = await Rejected(request);

        Assert.Equal(ErrorCode.BlocksMissing, error.Code);
        Assert.Equal([unknown], error.MissingBlocks);
    }

    [Fact]
    public async Task Put_over_unchanged_content_is_a_noop()
    {
        var first = await Ok(Put("x.txt", "same"));

        var again = await Ok(Put("x.txt", "same", baseRev: 0));

        Assert.Equal(first, again);
        Assert.Single(await Feed());
    }

    [Fact]
    public async Task Put_with_stale_base_revision_conflicts_and_returns_current_state()
    {
        var created = await Ok(Put("x.txt", "v1"));
        await Ok(Put("x.txt", "v2", baseRev: created.Revision));

        var error = await Rejected(Put("x.txt", "mine", baseRev: created.Revision));

        Assert.Equal(ErrorCode.RevisionConflict, error.Code);
        Assert.Equal(2, error.Current!.Revision);
        Assert.Equal("x.txt", error.Current.Path);
    }

    [Fact]
    public async Task Put_for_a_path_the_client_thought_was_free_conflicts_when_it_exists()
    {
        await Ok(Put("x.txt", "theirs"));

        var error = await Rejected(Put("x.txt", "mine", baseRev: 0));

        Assert.Equal(ErrorCode.RevisionConflict, error.Code);
    }

    [Fact]
    public async Task Put_on_a_folder_is_a_type_mismatch()
    {
        await Ok(Mkdir("a"));

        var error = await Rejected(Put("a", "x"));

        Assert.Equal(ErrorCode.TypeMismatch, error.Code);
    }

    [Fact]
    public async Task Names_collide_case_insensitively_and_keep_the_stored_spelling()
    {
        var first = await Ok(Put("Report.txt", "v1"));

        var second = await Ok(Put("report.txt", "v2", baseRev: first.Revision));

        Assert.Equal(first.NodeId, second.NodeId);
        Assert.Equal("Report.txt", (await Feed()).Last().Path);
    }

    [Fact]
    public async Task Names_are_normalized_to_nfc()
    {
        await Ok(Mkdir("Café"));

        Assert.Equal("Café", (await Feed()).Single().Path);
    }

    [Fact]
    public async Task Mkdir_twice_is_idempotent()
    {
        var first = await Ok(Mkdir("a"));

        Assert.Equal(first, await Ok(Mkdir("a")));
        Assert.Single(await Feed());
    }

    [Fact]
    public async Task Delete_of_a_folder_hides_its_subtree_with_a_single_feed_entry()
    {
        var dir = await Ok(Mkdir("a"));
        await Ok(Put("a/x.txt", "x"));

        var deleted = await Ok(Delete("a", dir.Revision + 1));

        var entry = (await Feed(after: 2)).Single();
        Assert.Equal((deleted.Revision, ChangeOps.Delete, "a"), (entry.Revision, entry.Op, entry.Path));
        Assert.Equal(ErrorCode.ParentMissing, (await Rejected(Put("a/y.txt", "y"))).Code);
    }

    [Fact]
    public async Task Delete_is_idempotent_and_unknown_paths_are_not_found()
    {
        var file = await Ok(Put("x.txt", "x"));
        await Ok(Delete("x.txt", file.Revision));

        await Ok(Delete("x.txt", file.Revision));

        Assert.Equal(ErrorCode.NotFound, (await Rejected(Delete("ghost.txt", 0))).Code);
        Assert.Equal(2, (await Feed()).Count());
    }

    [Fact]
    public async Task Delete_of_a_file_changed_elsewhere_conflicts()
    {
        var v1 = await Ok(Put("x.txt", "v1"));
        await Ok(Put("x.txt", "v2", baseRev: v1.Revision));

        Assert.Equal(ErrorCode.RevisionConflict, (await Rejected(Delete("x.txt", v1.Revision))).Code);
    }

    [Fact]
    public async Task Recreating_a_deleted_name_reuses_the_node_but_needs_the_deletion_revision()
    {
        var created = await Ok(Put("x.txt", "v1"));
        var deleted = await Ok(Delete("x.txt", created.Revision));

        Assert.Equal(ErrorCode.RevisionConflict, (await Rejected(Put("x.txt", "v2", baseRev: created.Revision))).Code);
        var recreated = await Ok(Put("x.txt", "v2", baseRev: deleted.Revision));

        Assert.Equal(created.NodeId, recreated.NodeId);
    }

    [Fact]
    public async Task Move_keeps_the_node_id_and_reports_old_and_new_path()
    {
        await Ok(Mkdir("a"));
        var file = await Ok(Put("a/x.txt", "x"));
        await Ok(Mkdir("b"));

        var moved = await Ok(Move("a/x.txt", "b/y.txt", file.Revision));

        var entry = (await Feed()).Last();
        Assert.Equal(file.NodeId, moved.NodeId);
        Assert.Equal((ChangeOps.Move, "b/y.txt", "a/x.txt"), (entry.Op, entry.Path, entry.OldPath));
        Assert.Equal(ErrorCode.NotFound, (await Rejected(Delete("a/x.txt", 0))).Code);
    }

    [Fact]
    public async Task Move_of_a_folder_does_not_touch_its_children()
    {
        var dir = await Ok(Mkdir("a"));
        await Ok(Put("a/x.txt", "x"));

        await Ok(Move("a", "b", dir.Revision));

        Assert.Equal(3, (await Feed()).Count());
        Assert.Equal(ErrorCode.RevisionConflict, (await Rejected(Put("b/x.txt", "y", baseRev: 0))).Code);
    }

    [Fact]
    public async Task Move_onto_an_existing_node_conflicts()
    {
        var a = await Ok(Put("a.txt", "a"));
        await Ok(Put("b.txt", "b"));

        var error = await Rejected(Move("a.txt", "b.txt", a.Revision));

        Assert.Equal(ErrorCode.AlreadyExists, error.Code);
    }

    [Fact]
    public async Task Move_onto_a_deleted_name_succeeds()
    {
        var a = await Ok(Put("a.txt", "a"));
        var b = await Ok(Put("b.txt", "b"));
        await Ok(Delete("b.txt", b.Revision));

        await Ok(Move("a.txt", "b.txt", a.Revision));

        Assert.Equal("b.txt", (await Feed()).Last().Path);
    }

    [Fact]
    public async Task Move_into_itself_is_rejected()
    {
        var dir = await Ok(Mkdir("a"));
        await Ok(Mkdir("a/b"));

        var error = await Rejected(Move("a", "a/b/c", dir.Revision));

        Assert.Equal(ErrorCode.InvalidPath, error.Code);
    }

    [Fact]
    public async Task Case_only_rename_is_allowed()
    {
        var file = await Ok(Put("x.txt", "x"));

        await Ok(Move("x.txt", "X.txt", file.Revision));

        Assert.Equal("X.txt", (await Feed()).Last().Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a/CON")]
    [InlineData("a:b")]
    public async Task Invalid_paths_are_rejected(string path)
    {
        Assert.Equal(ErrorCode.InvalidPath, (await Rejected(Mkdir(path))).Code);
    }

    [Fact]
    public async Task Unknown_operation_and_negative_base_are_invalid_requests()
    {
        Assert.Equal(ErrorCode.InvalidRequest, (await Rejected(new CommitRequest("explode", "a", 0))).Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await Rejected(Mkdir("a", baseRev: -1))).Code);
    }

    [Fact]
    public async Task Unknown_space_is_reported()
    {
        var result = await _service.CommitAsync("other", Mkdir("a"), null, Ct);

        Assert.Equal(ErrorCode.SpaceNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task Idempotency_key_replays_the_first_answer_without_a_second_change()
    {
        var first = await Ok(Mkdir("a"), key: "k1");

        var replay = await Ok(Mkdir("a"), key: "k1");
        var other = await Ok(Mkdir("b"), key: "k1"); // gleicher Schlüssel, anderer Inhalt: Antwort bleibt die erste

        Assert.Equal(first, replay);
        Assert.Equal(first, other);
        Assert.Single(await Feed());
    }

    [Fact]
    public async Task Failed_commits_do_not_consume_revisions()
    {
        await Rejected(Put("nope/x.txt", "x"));

        Assert.Equal(1, (await Ok(Mkdir("a"))).Revision);
    }

    [Fact]
    public async Task Feed_pages_and_reports_next_cursor()
    {
        for (var i = 0; i < 5; i++)
        {
            await Ok(Mkdir($"d{i}"));
        }

        var page = (await _service.GetChangesAsync(Space, 1, 2, Ct)).Value!;

        Assert.Equal([2L, 3L], page.Changes.Select(c => c.Revision));
        Assert.True(page.HasMore);
        Assert.Equal(3, page.NextCursor);
    }

    [Fact]
    public async Task Feed_without_news_keeps_the_cursor()
    {
        await Ok(Mkdir("a"));

        var page = (await _service.GetChangesAsync(Space, 1, 10, Ct)).Value!;

        Assert.Empty(page.Changes);
        Assert.Equal((false, 1L), (page.HasMore, page.NextCursor));
    }

    [Fact]
    public async Task Feed_cursor_before_the_retained_range_or_ahead_of_the_server_is_expired()
    {
        await Ok(Mkdir("a"));
        await Ok(Mkdir("b"));
        _fixture.TrimFeed(Space, 1);

        Assert.Equal(ErrorCode.CursorExpired, (await _service.GetChangesAsync(Space, 0, 10, Ct)).Error!.Code);
        Assert.Equal(ErrorCode.CursorExpired, (await _service.GetChangesAsync(Space, 9, 10, Ct)).Error!.Code);
        Assert.True((await _service.GetChangesAsync(Space, 1, 10, Ct)).IsSuccess);
    }

    [Fact]
    public async Task Feed_page_size_is_capped()
    {
        var result = await _service.GetChangesAsync(Space, 0, int.MaxValue, Ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Missing_blocks_lists_only_unknown_hashes_once()
    {
        var known = _fixture.AddBlock([1]).Value;
        var unknown = Fixtures.HashOf([2]).Value;

        var result = await _service.MissingBlocksAsync(Space, [known, unknown, unknown], uploaderId: null, Ct);

        Assert.Equal([unknown], result.Value!.Missing);
    }

    [Fact]
    public async Task Missing_blocks_rejects_malformed_hashes()
    {
        var result = await _service.MissingBlocksAsync(Space, ["sha256:xyz"], uploaderId: null, Ct);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task Put_block_stores_matching_content_and_rejects_mismatch()
    {
        var content = new byte[] { 9, 9, 9 };
        var hash = Fixtures.HashOf(content).Value;

        var good = await _service.PutBlockAsync(hash, new MemoryStream(content), uploaderId: null, Ct);
        var bad = await _service.PutBlockAsync(hash, new MemoryStream([1]), uploaderId: null, Ct);

        Assert.True(good.IsSuccess);
        Assert.Equal(ErrorCode.BlockHashMismatch, bad.Error!.Code);
        Assert.Empty((await _service.MissingBlocksAsync(Space, [hash], uploaderId: null, Ct)).Value!.Missing);
    }

    [Fact]
    public async Task Get_node_returns_the_block_list_and_current_revision()
    {
        var put = Put("docs.txt", "inhalt");
        var created = await Ok(put);

        var detail = (await _service.GetNodeAsync(Space, "docs.txt", Ct)).Value!;

        Assert.Equal(put.Blocks, detail.Blocks);
        Assert.Equal((created.NodeId, created.Revision, "docs.txt", false), (detail.Node.NodeId, detail.Node.Revision, detail.Node.Path, detail.Node.Deleted));
    }

    [Fact]
    public async Task Get_node_reports_a_deleted_node_as_tombstone_without_blocks()
    {
        var created = await Ok(Put("gone.txt", "x"));
        var deleted = await Ok(Delete("gone.txt", created.Revision));

        var detail = (await _service.GetNodeAsync(Space, "gone.txt", Ct)).Value!;

        Assert.True(detail.Node.Deleted);
        Assert.Equal(deleted.Revision, detail.Node.Revision);
        Assert.Empty(detail.Blocks);
    }

    [Fact]
    public async Task Get_node_rejects_unknown_missing_and_root_paths()
    {
        Assert.Equal(ErrorCode.NotFound, (await _service.GetNodeAsync(Space, "nope.txt", Ct)).Error!.Code);
        Assert.Equal(ErrorCode.NotFound, (await _service.GetNodeAsync(Space, "no/such/file.txt", Ct)).Error!.Code);
        Assert.Equal(ErrorCode.InvalidPath, (await _service.GetNodeAsync(Space, string.Empty, Ct)).Error!.Code);
        Assert.Equal(ErrorCode.SpaceNotFound, (await _service.GetNodeAsync("other", "a", Ct)).Error!.Code);
    }

    [Fact]
    public async Task Snapshot_lists_live_nodes_recursively_sorted_by_path()
    {
        await Ok(Mkdir("b"));
        await Ok(Mkdir("a"));
        await Ok(Put("a/z.txt", "z"));
        await Ok(Put("a/y.txt", "y"));
        var gone = await Ok(Put("b/old.txt", "o"));
        await Ok(Delete("b/old.txt", gone.Revision));

        var all = (await _service.GetSnapshotAsync(Space, string.Empty, Ct)).Value!;
        var sub = (await _service.GetSnapshotAsync(Space, "a", Ct)).Value!;

        Assert.Equal(["a", "a/y.txt", "a/z.txt", "b"], all.Select(e => e.Path));
        Assert.Equal(["a/y.txt", "a/z.txt"], sub.Select(e => e.Path));
    }

    [Fact]
    public async Task Snapshot_of_a_file_is_just_that_file_and_of_a_missing_path_is_not_found()
    {
        await Ok(Put("x.txt", "x"));

        var file = (await _service.GetSnapshotAsync(Space, "x.txt", Ct)).Value!;

        Assert.Equal("x.txt", Assert.Single(file).Path);
        Assert.Equal(ErrorCode.NotFound, (await _service.GetSnapshotAsync(Space, "missing", Ct)).Error!.Code);
    }

    [Fact]
    public async Task Empty_file_has_no_blocks_and_nonempty_file_needs_some()
    {
        await Ok(Put("empty.txt", string.Empty));

        var hash = Fixtures.HashOf([]).Value;
        var bad = new CommitRequest(CommitOps.Put, "bad.txt", 0, Blocks: [], Size: 5, MTime: T0, FileHash: hash);
        Assert.Equal(ErrorCode.InvalidRequest, (await Rejected(bad)).Code);
    }
}
