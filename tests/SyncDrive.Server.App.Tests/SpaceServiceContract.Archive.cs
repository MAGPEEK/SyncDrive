using SyncDrive.Contracts;

namespace SyncDrive.Server.App.Tests;

/// <summary>ZIP-Download (M6.3): was ins Archiv gehört, gegen jede Store-Implementierung.</summary>
public abstract partial class SpaceServiceContract
{
    [Fact]
    public async Task An_archive_holds_the_live_tree_of_the_selection_with_names_below_its_folder()
    {
        await Ok(Mkdir("Projekte"));
        await Ok(Mkdir("Projekte/Alt"));
        await Ok(Put("Projekte/Alt/a.txt", "a"));
        var gone = await Ok(Put("Projekte/weg.txt", "weg"));
        await Ok(Delete("Projekte/weg.txt", gone.Revision));
        await Ok(Put("Projekte/b.txt", "bb"));

        var plan = await Service.PlanArchiveAsync(SpaceId, ["Projekte/Alt", "Projekte/b.txt"], Ct);

        Assert.True(plan.IsSuccess);
        Assert.Equal(["Alt/", "Alt/a.txt", "b.txt"], plan.Value!.Select(e => e.Name));
        Assert.Equal([0L, 1L, 2L], plan.Value!.Select(e => e.Size));
        Assert.Equal([0, 1, 1], plan.Value!.Select(e => e.Blocks.Count));
    }

    [Fact]
    public async Task An_archive_rejects_empty_mixed_and_deleted_selections()
    {
        await Ok(Mkdir("A"));
        await Ok(Put("A/x.txt", "x"));
        var gone = await Ok(Put("y.txt", "y"));
        await Ok(Delete("y.txt", gone.Revision));

        var empty = await Service.PlanArchiveAsync(SpaceId, [], Ct);
        var mixed = await Service.PlanArchiveAsync(SpaceId, ["A/x.txt", "y.txt"], Ct);
        var deleted = await Service.PlanArchiveAsync(SpaceId, ["y.txt"], Ct);
        var root = await Service.PlanArchiveAsync(SpaceId, [""], Ct);

        Assert.Equal(
            (ErrorCode.InvalidRequest, ErrorCode.InvalidRequest, ErrorCode.NotFound, ErrorCode.InvalidPath),
            (empty.Error!.Code, mixed.Error!.Code, deleted.Error!.Code, root.Error!.Code));
    }
}
