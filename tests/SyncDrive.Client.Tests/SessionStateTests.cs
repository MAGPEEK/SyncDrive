using SyncDrive.Client.App.Ports;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.Sqlite.Client;
using SyncDrive.Testing;

namespace SyncDrive.Client.Tests;

/// <summary>Gleiches Verhalten für jede <see cref="ISessionState"/>-Implementierung.</summary>
public abstract class SessionStateContract
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, 123, TimeSpan.Zero);

    protected abstract ISessionState State { get; }

    private static RelativePath P(string p) => RelativePath.Parse(p);

    private static SyncedNode File(string path, long rev = 1) =>
        new(P(path), "n_" + path, NodeState.File(5, T0.AddTicks(4567), ContentHash.FromBytes(new byte[32])), rev, T0);

    private static SyncedNode Dir(string path) => new(P(path), "n_" + path, NodeState.Directory(T0), 1, T0);

    private async Task<List<string>> List(string root)
    {
        var paths = new List<string>();
        await foreach (var node in State.ListAsync(P(root), Ct))
        {
            paths.Add(node.Path.Value);
        }

        return paths;
    }

    [Fact]
    public async Task Saved_nodes_round_trip_exactly()
    {
        var file = File("Ordner/Straße.txt", rev: 42);
        await State.SaveAsync(file, Ct);

        Assert.Equal(file, await State.GetAsync(P("Ordner/Straße.txt"), Ct));
        Assert.Null(await State.GetAsync(P("fehlt.txt"), Ct));
    }

    [Fact]
    public async Task Lookups_ignore_case_and_saving_again_replaces()
    {
        await State.SaveAsync(File("Doc.txt", rev: 1), Ct);
        await State.SaveAsync(File("doc.TXT", rev: 2), Ct);

        Assert.Equal(2, (await State.GetAsync(P("DOC.txt"), Ct))!.RemoteRevision);
    }

    [Fact]
    public async Task Remove_subtree_removes_the_path_and_its_descendants_only()
    {
        await State.SaveAsync(Dir("a"), Ct);
        await State.SaveAsync(File("a/x.txt"), Ct);
        await State.SaveAsync(File("a/sub/y.txt"), Ct);
        await State.SaveAsync(File("ab.txt"), Ct);

        await State.RemoveSubtreeAsync(P("A"), Ct);

        Assert.Equal(["ab.txt"], await List(string.Empty));
    }

    [Fact]
    public async Task List_returns_descendants_without_the_root_sorted()
    {
        await State.SaveAsync(File("b/2.txt"), Ct);
        await State.SaveAsync(Dir("b"), Ct);
        await State.SaveAsync(File("b/1.txt"), Ct);
        await State.SaveAsync(File("bb.txt"), Ct);

        Assert.Equal(["b/1.txt", "b/2.txt"], await List("b"));
        Assert.Equal(["b", "b/1.txt", "b/2.txt", "bb.txt"], await List(string.Empty));
    }

    [Fact]
    public async Task Names_with_like_wildcards_are_not_special()
    {
        await State.SaveAsync(File("100%_echt/a.txt"), Ct);
        await State.SaveAsync(File("100x_echt/b.txt"), Ct);

        Assert.Equal(["100%_echt/a.txt"], await List("100%_echt"));
    }

    [Fact]
    public async Task Feed_cursor_starts_at_zero_and_persists()
    {
        Assert.Equal(0, await State.GetFeedCursorAsync(Ct));

        await State.SetFeedCursorAsync(1042, Ct);

        Assert.Equal(1042, await State.GetFeedCursorAsync(Ct));
    }
}

public sealed class InMemorySessionStateTests : SessionStateContract
{
    protected override ISessionState State { get; } = new InMemorySessionState();
}

public sealed class SqliteSessionStateTests : SessionStateContract, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "syncdrive-state-" + Guid.NewGuid().ToString("N"));

    public SqliteSessionStateTests()
    {
        Directory.CreateDirectory(_dir);
        State = new SqliteSessionState(Path.Combine(_dir, "session.db"));
    }

    protected override ISessionState State { get; }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task State_survives_reopening()
    {
        await State.SaveAsync(new SyncedNode(RelativePath.Parse("x.txt"), "n1", NodeState.Directory(DateTimeOffset.UnixEpoch), 3, DateTimeOffset.UnixEpoch), CancellationToken.None);
        await State.SetFeedCursorAsync(7, CancellationToken.None);

        var reopened = new SqliteSessionState(Path.Combine(_dir, "session.db"));

        Assert.NotNull(await reopened.GetAsync(RelativePath.Parse("x.txt"), CancellationToken.None));
        Assert.Equal(7, await reopened.GetFeedCursorAsync(CancellationToken.None));
    }
}

public sealed class SqliteActivityLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "syncdrive-activity-" + Guid.NewGuid().ToString("N"));

    public SqliteActivityLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Recent_entries_come_newest_first_with_all_fields()
    {
        var log = new SqliteActivityLog(Path.Combine(_dir, "session.db"));
        var t0 = new DateTimeOffset(2026, 10, 19, 9, 57, 35, TimeSpan.Zero);
        await log.AppendAsync(new ActivityEntry(t0, ActivityAction.Upload, RelativePath.Parse("a.txt"), false, ActivityResult.Done), CancellationToken.None);
        await log.AppendAsync(new ActivityEntry(t0.AddSeconds(1), ActivityAction.Delete, RelativePath.Parse("~$b.docx"), false, ActivityResult.Unsynced, "Name"), CancellationToken.None);
        await log.AppendAsync(new ActivityEntry(t0.AddSeconds(2), ActivityAction.Download, RelativePath.Parse("Ordner"), true, ActivityResult.Done), CancellationToken.None);

        var recent = await log.RecentAsync(2, CancellationToken.None);

        Assert.Equal(["Ordner", "~$b.docx"], recent.Select(e => e.Path.Value));
        Assert.Equal(new ActivityEntry(t0.AddSeconds(1), ActivityAction.Delete, RelativePath.Parse("~$b.docx"), false, ActivityResult.Unsynced, "Name"), recent[1]);
        Assert.True(recent[0].IsDirectory);
    }
}
