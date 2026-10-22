using SyncDrive.Core.Merge;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Core.Tests;

public class MergeEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly RelativePath Path = RelativePath.Parse("a/b.txt");

    private static ContentHash Hash(char c) => ContentHash.Parse("sha256:" + new string(c, 64));

    private static NodeState File(char content, int minutes = 0) =>
        NodeState.File(10, T0.AddMinutes(minutes), Hash(content));

    private static readonly NodeState Dir = NodeState.Directory(T0);

    private static SyncAction Decide(NodeState? b, NodeState? l, NodeState? r, MergeOptions? options = null) =>
        MergeEngine.Decide(new PathTriple(Path, b, l, r), options ?? new MergeOptions());

    // ---- Grundtabelle (bidirektional) ----

    [Fact]
    public void Nothing_changed_does_nothing() =>
        Assert.IsType<SyncAction.NoOp>(Decide(File('a'), File('a'), File('a')));

    [Fact]
    public void Nothing_exists_does_nothing() =>
        Assert.IsType<SyncAction.NoOp>(Decide(null, null, null));

    [Fact]
    public void Local_change_uploads() =>
        Assert.IsType<SyncAction.Upload>(Decide(File('a'), File('b'), File('a')));

    [Fact]
    public void Local_new_file_uploads() =>
        Assert.IsType<SyncAction.Upload>(Decide(null, File('a'), null));

    [Fact]
    public void Local_delete_deletes_remote() =>
        Assert.IsType<SyncAction.RemoteDelete>(Decide(File('a'), null, File('a')));

    [Fact]
    public void Remote_change_downloads() =>
        Assert.IsType<SyncAction.Download>(Decide(File('a'), File('a'), File('b')));

    [Fact]
    public void Remote_new_file_downloads() =>
        Assert.IsType<SyncAction.Download>(Decide(null, null, File('a')));

    [Fact]
    public void Remote_delete_deletes_local() =>
        Assert.IsType<SyncAction.LocalDelete>(Decide(File('a'), File('a'), null));

    [Fact]
    public void Same_change_on_both_sides_only_adopts_base() =>
        Assert.IsType<SyncAction.AdoptBase>(Decide(File('a'), File('b'), File('b')));

    [Fact]
    public void Both_deleted_only_adopts_base() =>
        Assert.IsType<SyncAction.AdoptBase>(Decide(File('a'), null, null));

    [Fact]
    public void Same_new_file_on_both_sides_only_adopts_base() =>
        Assert.IsType<SyncAction.AdoptBase>(Decide(null, File('a'), File('a')));

    [Fact]
    public void Touched_mtime_with_same_hash_is_not_a_change() =>
        Assert.IsType<SyncAction.NoOp>(Decide(File('a'), File('a', minutes: 5), File('a')));

    [Fact]
    public void Missing_hash_falls_back_to_weak_check()
    {
        var unhashed = NodeState.File(10, T0);
        var changed = NodeState.File(10, T0.AddMinutes(1));
        Assert.IsType<SyncAction.NoOp>(Decide(unhashed, unhashed, unhashed));
        Assert.IsType<SyncAction.Upload>(Decide(unhashed, changed, unhashed));
    }

    // ---- Konflikte ----

    [Fact]
    public void Both_modified_differently_conflicts_and_keeps_both_by_default()
    {
        var action = Decide(File('a'), File('b'), File('c'));
        Assert.Equal(new SyncAction.Conflict(ConflictKind.ModifyModify, Side.Remote, LoserHandling.CopyAside), action);
    }

    [Fact]
    public void Add_add_with_different_content_conflicts() =>
        Assert.Equal(
            new SyncAction.Conflict(ConflictKind.AddAdd, Side.Remote, LoserHandling.CopyAside),
            Decide(null, File('a'), File('b')));

    [Fact]
    public void File_versus_directory_is_type_change() =>
        Assert.Equal(
            new SyncAction.Conflict(ConflictKind.TypeChange, Side.Remote, LoserHandling.CopyAside),
            Decide(File('a'), File('a') with { Size = 11 }, Dir));

    [Fact]
    public void Local_modified_remote_deleted_keeps_the_modification()
    {
        var action = Decide(File('a'), File('b'), null);
        Assert.Equal(new SyncAction.Conflict(ConflictKind.ModifyDelete, Side.Local, LoserHandling.None), action);
    }

    [Fact]
    public void Local_deleted_remote_modified_keeps_the_modification()
    {
        var action = Decide(File('a'), null, File('b'));
        Assert.Equal(new SyncAction.Conflict(ConflictKind.DeleteModify, Side.Remote, LoserHandling.None), action);
    }

    [Theory]
    [InlineData(ConflictPolicy.ClientWins, Side.Local)]
    [InlineData(ConflictPolicy.ServerWins, Side.Remote)]
    public void Explicit_policy_picks_its_side_in_modify_modify(ConflictPolicy policy, Side winner)
    {
        var options = new MergeOptions(Policy: policy, RenameConflict: false);
        var action = Assert.IsType<SyncAction.Conflict>(Decide(File('a'), File('b'), File('c'), options));
        Assert.Equal(winner, action.Winner);
        Assert.Equal(LoserHandling.Trash, action.Loser);
    }

    [Fact]
    public void Rename_conflict_copies_the_loser_aside()
    {
        var options = new MergeOptions(Policy: ConflictPolicy.ClientWins, RenameConflict: true);
        var action = Assert.IsType<SyncAction.Conflict>(Decide(File('a'), File('b'), File('c'), options));
        Assert.Equal(LoserHandling.CopyAside, action.Loser);
    }

    [Theory]
    [InlineData(5, 0, Side.Local)]
    [InlineData(0, 5, Side.Remote)]
    [InlineData(3, 3, Side.Remote)] // Gleichstand: Server
    public void Newer_wins_compares_mtime(int localMinutes, int remoteMinutes, Side expected)
    {
        var options = new MergeOptions(Policy: ConflictPolicy.NewerWins);
        var action = Assert.IsType<SyncAction.Conflict>(
            Decide(File('a'), File('b', localMinutes), File('c', remoteMinutes), options));
        Assert.Equal(expected, action.Winner);
    }

    [Fact]
    public void Client_wins_lets_local_delete_beat_remote_modification()
    {
        var options = new MergeOptions(Policy: ConflictPolicy.ClientWins);
        var action = Decide(File('a'), null, File('b'), options);
        Assert.Equal(new SyncAction.Conflict(ConflictKind.DeleteModify, Side.Local, LoserHandling.Trash), action);
    }

    [Fact]
    public void Server_wins_lets_remote_delete_beat_local_modification()
    {
        var options = new MergeOptions(Policy: ConflictPolicy.ServerWins);
        var action = Decide(File('a'), File('b'), null, options);
        Assert.Equal(new SyncAction.Conflict(ConflictKind.ModifyDelete, Side.Remote, LoserHandling.Trash), action);
    }

    // ---- Richtung und Lösch-Optionen ----

    [Fact]
    public void Upload_only_ignores_remote_changes() =>
        Assert.IsType<SyncAction.NoOp>(Decide(File('a'), File('a'), File('b'), new(Direction: SyncDirection.UploadOnly)));

    [Fact]
    public void Upload_only_uploads_even_when_remote_also_changed() =>
        Assert.IsType<SyncAction.Upload>(
            Decide(File('a'), File('b'), File('c'), new(Direction: SyncDirection.UploadOnly)));

    [Fact]
    public void Download_only_ignores_local_changes() =>
        Assert.IsType<SyncAction.NoOp>(
            Decide(File('a'), File('b'), File('a'), new(Direction: SyncDirection.DownloadOnly)));

    [Fact]
    public void Download_only_downloads_even_when_local_also_changed() =>
        Assert.IsType<SyncAction.Download>(
            Decide(File('a'), File('b'), File('c'), new(Direction: SyncDirection.DownloadOnly)));

    [Fact]
    public void Ignore_local_delete_does_not_delete_remote() =>
        Assert.IsType<SyncAction.NoOp>(Decide(File('a'), null, File('a'), new(IgnoreLocalDelete: true)));

    [Fact]
    public void Ignore_local_delete_still_restores_a_remotely_changed_file() =>
        Assert.IsType<SyncAction.Download>(Decide(File('a'), null, File('b'), new(IgnoreLocalDelete: true)));

    [Fact]
    public void Ignore_remote_delete_does_not_delete_local() =>
        Assert.IsType<SyncAction.NoOp>(Decide(File('a'), File('a'), null, new(IgnoreRemoteDelete: true)));

    [Fact]
    public void Ignore_remote_delete_keeps_local_modification_without_conflict() =>
        Assert.IsType<SyncAction.Upload>(Decide(File('a'), File('b'), null, new(IgnoreRemoteDelete: true)));

    [Fact]
    public void One_way_modes_let_the_authoritative_side_win()
    {
        // Upload-only: lokal gelöscht, remote geändert -> Remote wird gelöscht (Server hält Version/Papierkorb).
        Assert.IsType<SyncAction.RemoteDelete>(
            Decide(File('a'), null, File('b'), new(Direction: SyncDirection.UploadOnly)));

        // Download-only: remote gelöscht, lokal geändert -> lokal wird gelöscht (Handler nutzt den lokalen Papierkorb).
        Assert.IsType<SyncAction.LocalDelete>(
            Decide(File('a'), File('b'), null, new(Direction: SyncDirection.DownloadOnly)));
    }

    // ---- Invarianten über den gesamten Zustandsraum ----

    public static TheoryData<int, int, int, int> AllCombinations()
    {
        var data = new TheoryData<int, int, int, int>();
        for (var o = 0; o < Options.Length; o++)
        {
            for (var b = 0; b < States.Length; b++)
            {
                for (var l = 0; l < States.Length; l++)
                {
                    for (var r = 0; r < States.Length; r++)
                    {
                        data.Add(o, b, l, r);
                    }
                }
            }
        }

        return data;
    }

    private static readonly NodeState?[] States =
    [
        null, Dir, File('a'), File('b'), File('c', minutes: 9),
    ];

    private static readonly MergeOptions[] Options =
    [
        new(),
        new(Policy: ConflictPolicy.NewerWins),
        new(Policy: ConflictPolicy.ClientWins, RenameConflict: false),
        new(Policy: ConflictPolicy.ServerWins, RenameConflict: false),
        new(Direction: SyncDirection.UploadOnly),
        new(Direction: SyncDirection.DownloadOnly),
        new(IgnoreLocalDelete: true, IgnoreRemoteDelete: true),
    ];

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Decision_never_destroys_unsynced_data(int o, int b, int l, int r)
    {
        var (options, baseState, local, remote) = (Options[o], States[b], States[l], States[r]);
        var action = Decide(baseState, local, remote, options);

        // Einweg-Modi: Die maßgebliche Seite gewinnt bewusst auch gegen eine geänderte Gegenseite
        // (siehe One_way_modes_let_the_authoritative_side_win). Hier gelten die Zwei-Wege-Garantien.
        if (options.Direction != SyncDirection.TwoWay)
        {
            return;
        }

        switch (action)
        {
            case SyncAction.LocalDelete:
                // Lokal wird nur gelöscht, was seit dem letzten Abgleich unverändert ist.
                Assert.True(NodeState.Same(local, baseState));
                Assert.Null(remote);
                break;
            case SyncAction.RemoteDelete:
                Assert.True(NodeState.Same(remote, baseState));
                Assert.Null(local);
                break;
            case SyncAction.Upload:
                Assert.NotNull(local);
                Assert.False(NodeState.Same(local, baseState));
                break;
            case SyncAction.Download:
                Assert.NotNull(remote);
                Assert.False(NodeState.Same(remote, baseState));
                break;
            case SyncAction.AdoptBase:
                Assert.True(NodeState.Same(local, remote));
                break;
            case SyncAction.Conflict c:
                Assert.False(NodeState.Same(local, remote));
                // Bei KeepBoth und Newer-Wins wird kein Inhalt verworfen.
                if (options.Policy is ConflictPolicy.KeepBoth or ConflictPolicy.NewerWins)
                {
                    Assert.NotEqual(LoserHandling.Trash, c.Loser);
                }

                break;
        }
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Decision_is_stable_after_applying_it(int o, int b, int l, int r)
    {
        // Nach AdoptBase ist die Basis gleich beiden Seiten; eine erneute Bewertung ändert nichts mehr.
        var options = Options[o];
        if (Decide(States[b], States[l], States[r], options) is SyncAction.AdoptBase)
        {
            Assert.IsType<SyncAction.NoOp>(Decide(States[l], States[l], States[r], options));
        }
    }
}
