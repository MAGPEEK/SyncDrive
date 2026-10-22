using SyncDrive.Contracts;
using SyncDrive.Core.Paths;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Tests;

/// <summary>Geteilter Ordner als eigener Space (M6b, ADR-12): Pfade, Wurzelschutz, Feed-Filter, gegen jede Store-Implementierung.</summary>
public abstract partial class SpaceServiceContract
{
    private SpaceView View(string root, SpaceRole role = SpaceRole.Editor)
    {
        Assert.True(RelativePath.TryParse(root, out var parsed));
        return new SpaceView(Service, "sh_test", SpaceId, parsed, role);
    }

    private async Task<long> Cursor() => (await Service.GetLatestRevisionAsync(SpaceId, Ct)).Value!.Latest;

    [Fact]
    public async Task A_view_shows_paths_below_the_shared_folder_and_hides_the_rest()
    {
        await Ok(Mkdir("Projekte"));
        await Ok(Put("Projekte/plan.txt", "plan"));
        var secret = await Ok(Put("geheim.txt", "geheim"));
        var view = View("Projekte");

        var snapshot = await view.GetSnapshotAsync(string.Empty, Ct);
        var node = await view.GetNodeAsync("plan.txt", Ct);
        var escape = await view.GetNodeAsync("../geheim.txt", Ct);
        var found = await view.SearchAsync(string.Empty, "t", 50, Ct);
        var byId = await view.GetNodeByIdAsync(secret.NodeId, Ct);

        Assert.Equal(["plan.txt"], snapshot.Value!.Select(e => e.Path));
        Assert.Equal("plan.txt", node.Value!.Node.Path);
        Assert.Equal(ErrorCode.InvalidPath, escape.Error!.Code);
        Assert.Equal(["plan.txt"], found.Value!.Select(e => e.Path));
        Assert.Equal(ErrorCode.NotFound, byId.Error!.Code);
    }

    [Fact]
    public async Task Commits_through_a_view_land_inside_and_the_shared_folder_itself_is_protected()
    {
        await Ok(Mkdir("Projekte"));
        var plan = await Ok(Put("Projekte/plan.txt", "plan"));
        var view = View("Projekte");

        var put = await view.CommitAsync(await UploadedByAsync(Anna, Put("neu.txt", "neu")), null, Anna, Ct);
        var deleteRoot = await view.CommitAsync(new CommitRequest(CommitOps.Delete, string.Empty, 1), null, Anna, Ct);
        var moveOut = await view.CommitAsync(Move("plan.txt", "../plan.txt", plan.Revision), null, Anna, Ct);
        var conflict = await view.CommitAsync(await UploadedByAsync(Anna, Put("plan.txt", "anders", baseRev: 1)), null, Anna, Ct);

        Assert.True(put.IsSuccess);
        Assert.False((await Service.GetNodeAsync(SpaceId, "Projekte/neu.txt", Ct)).Value!.Node.Deleted);
        Assert.Equal((ErrorCode.InvalidPath, ErrorCode.InvalidPath), (deleteRoot.Error!.Code, moveOut.Error!.Code));
        Assert.Equal("plan.txt", conflict.Error!.Current!.Path); // auch der mitgelieferte Knoten ist relativ zur Wurzel
    }

    [Fact]
    public async Task The_feed_of_a_view_turns_moves_across_its_border_into_create_and_delete()
    {
        await Ok(Mkdir("Projekte"));
        var inside = await Ok(Put("Projekte/a.txt", "a"));
        var outside = await Ok(Put("draußen.txt", "d"));
        var after = await Cursor();
        await Ok(Put("Projekte/b.txt", "b"));
        await Ok(Put("sonstwo.txt", "s"));
        await Ok(Move("draußen.txt", "Projekte/herein.txt", outside.Revision));
        await Ok(Move("Projekte/a.txt", "hinaus.txt", inside.Revision));

        var page = (await View("Projekte").GetChangesAsync(after, 100, Ct)).Value!;

        Assert.Equal(
            [(ChangeOps.Upsert, "b.txt"), (ChangeOps.Upsert, "herein.txt"), (ChangeOps.Delete, "a.txt")],
            page.Changes.Select(c => (c.Op, c.Path)));
        Assert.All(page.Changes, c => Assert.Null(c.OldPath));
        Assert.Equal(await Cursor(), page.NextCursor);
    }

    [Fact]
    public async Task A_folder_moved_in_or_a_moved_shared_folder_expires_the_cursor_of_the_view()
    {
        await Ok(Mkdir("Projekte"));
        var outsideFolder = await Ok(Mkdir("Draußen"));
        var beforeFolderMove = await Cursor();
        await Ok(Move("Draußen", "Projekte/Draußen", outsideFolder.Revision));
        var beforeRootMove = await Cursor();
        var root = await Service.GetNodeAsync(SpaceId, "Projekte", Ct);
        await Ok(Move("Projekte", "Projekte 2026", root.Value!.Node.Revision));

        var folderIn = await View("Projekte").GetChangesAsync(beforeFolderMove, 100, Ct);
        var rootMoved = await View("Projekte 2026").GetChangesAsync(beforeRootMove, 100, Ct);

        Assert.Equal((ErrorCode.CursorExpired, ErrorCode.CursorExpired), (folderIn.Error!.Code, rootMoved.Error!.Code));
    }

    [Fact]
    public async Task Waiting_on_a_view_skips_changes_outside_but_moves_the_cursor_on()
    {
        await Ok(Mkdir("Projekte"));
        var after = await Cursor();
        await Ok(Put("draußen.txt", "d"));

        var quiet = (await View("Projekte").WaitForChangesAsync(after, 100, TimeSpan.FromMilliseconds(100), Ct)).Value!;
        await Ok(Put("Projekte/drin.txt", "d"));
        var busy = (await View("Projekte").WaitForChangesAsync(after, 100, TimeSpan.FromSeconds(5), Ct)).Value!;

        Assert.Empty(quiet.Changes);
        Assert.Equal(after + 1, quiet.NextCursor);
        Assert.Equal(["drin.txt"], busy.Changes.Select(c => c.Path));
    }

    [Fact]
    public async Task The_recycle_bin_of_a_view_holds_only_its_own_deleted_items()
    {
        await Ok(Mkdir("Projekte"));
        var inside = await Ok(Put("Projekte/a.txt", "a"));
        var outside = await Ok(Put("geheim.txt", "g"));
        await Ok(Delete("Projekte/a.txt", inside.Revision));
        await Ok(Delete("geheim.txt", outside.Revision));
        var view = View("Projekte");

        var trash = (await view.ListTrashAsync(Ct)).Value!;
        var foreign = await view.RestoreFromTrashAsync(outside.NodeId, Anna, Ct);
        var own = await view.RestoreFromTrashAsync(inside.NodeId, Anna, Ct);

        Assert.Equal([("a.txt", "a.txt")], trash.Entries.Select(e => (e.Name, e.OriginalPath)));
        Assert.Equal(ErrorCode.NotFound, foreign.Error!.Code);
        Assert.True(own.IsSuccess);
    }

    [Fact]
    public async Task A_view_of_the_whole_space_changes_nothing()
    {
        await Ok(Mkdir("Projekte"));
        await Ok(Put("Projekte/a.txt", "a"));
        var view = View(string.Empty);

        var snapshot = await view.GetSnapshotAsync(string.Empty, Ct);
        var feed = await view.GetChangesAsync(0, 100, Ct);

        Assert.False(view.IsScoped);
        Assert.Equal(["Projekte", "Projekte/a.txt"], snapshot.Value!.Select(e => e.Path));
        Assert.Equal(2, feed.Value!.Changes.Count);
    }
}
