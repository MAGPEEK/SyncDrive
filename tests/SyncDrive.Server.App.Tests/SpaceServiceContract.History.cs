using SyncDrive.Contracts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Tests;

/// <summary>Versionen, Kopieren, Papierkorb und Sperren (M4b), gegen jede Store-Implementierung.</summary>
public abstract partial class SpaceServiceContract
{
    private const string Anna = "u_anna";
    private const string Ben = "u_ben";

    private async Task<CommitResponse> OkAs(string actor, CommitRequest request)
    {
        var result = await Service.CommitAsync(SpaceId, await UploadedByAsync(actor, request), null, actor, Ct);
        Assert.True(result.IsSuccess, result.Error?.Code + ": " + result.Error?.Message);
        return result.Value!;
    }

    /// <summary>Wie ein echter Client: die Blöcke des Commits erst als <paramref name="actor"/> hochladen (0.15.0: nur eigene oder schon im Space).</summary>
    private async Task<CommitRequest> UploadedByAsync(string actor, CommitRequest request)
    {
        foreach (var hash in request.Blocks ?? [])
        {
            await using var content = await _fixture.Blocks.OpenReadAsync(Core.Model.ContentHash.Parse(hash), Ct);
            if (content is not null)
            {
                await Service.PutBlockAsync(hash, content, actor, Ct);
            }
        }

        return request;
    }

    private async Task<ServiceError> RejectedAs(string actor, CommitRequest request)
    {
        var result = await Service.CommitAsync(SpaceId, await UploadedByAsync(actor, request), null, actor, Ct);
        Assert.False(result.IsSuccess);
        return result.Error!;
    }

    private async Task<IReadOnlyList<VersionInfo>> Versions(string path) =>
        (await Service.ListVersionsAsync(SpaceId, path, Ct)).Value!.Versions;

    private async Task<string> HashOf(string path) =>
        (await Service.GetNodeAsync(SpaceId, path, Ct)).Value!.Node.Hash!;

    // ---------- Versionen ----------

    [Fact]
    public async Task Every_content_change_becomes_a_version_newest_first_with_author()
    {
        var v1 = await OkAs(Anna, Put("doc.txt", "eins"));
        var v2 = await OkAs(Ben, Put("doc.txt", "zwei", v1.Revision));

        var versions = await Versions("doc.txt");

        Assert.Equal([v2.Revision, v1.Revision], versions.Select(v => v.Revision));
        Assert.Equal([Ben, Anna], versions.Select(v => v.CreatedBy));
    }

    [Fact]
    public async Task Only_the_configured_number_of_versions_is_kept()
    {
        var rev = 0L;
        for (var i = 1; i <= 5; i++)
        {
            rev = (await Ok(Put("doc.txt", $"Fassung {i}", rev))).Revision;
        }

        var versions = await Versions("doc.txt");

        Assert.Equal(3, versions.Count);
        Assert.Equal(rev, versions[0].Revision);
    }

    [Fact]
    public async Task Saving_identical_content_creates_no_version()
    {
        var first = await Ok(Put("doc.txt", "gleich"));

        await Ok(Put("doc.txt", "gleich", first.Revision));

        Assert.Single(await Versions("doc.txt"));
    }

    [Fact]
    public async Task Restoring_a_version_makes_it_current_as_a_new_revision()
    {
        var v1 = await Ok(Put("doc.txt", "original"));
        var hashV1 = await HashOf("doc.txt");
        var v2 = await Ok(Put("doc.txt", "überschrieben", v1.Revision));

        var restored = await Service.RestoreVersionAsync(SpaceId, new RestoreVersionRequest("doc.txt", v1.Revision, v2.Revision), Anna, Ct);

        Assert.True(restored.IsSuccess);
        Assert.True(restored.Value!.Revision > v2.Revision);
        Assert.Equal(hashV1, await HashOf("doc.txt"));
        Assert.Equal(3, (await Versions("doc.txt")).Count);
        Assert.Equal(ErrorCode.NotFound,
            (await Service.RestoreVersionAsync(SpaceId, new RestoreVersionRequest("doc.txt", 999, restored.Value.Revision), Anna, Ct)).Error!.Code);
    }

    // ---------- Kopieren ----------

    [Fact]
    public async Task Copying_a_file_creates_a_new_node_with_the_same_content()
    {
        var original = await Ok(Put("a.txt", "Inhalt"));

        var copy = await Ok(new CommitRequest(CommitOps.Copy, "a.txt", 0, NewPath: "Kopie von a.txt"));

        var copied = (await Service.GetNodeAsync(SpaceId, "Kopie von a.txt", Ct)).Value!;
        Assert.NotEqual(original.NodeId, copy.NodeId);
        Assert.Equal(await HashOf("a.txt"), copied.Node.Hash);
        Assert.Single(await Versions("Kopie von a.txt"));
    }

    [Fact]
    public async Task Copying_a_folder_copies_the_whole_tree_and_announces_every_new_node()
    {
        await Ok(Mkdir("Vorlage"));
        await Ok(Mkdir("Vorlage/Unter"));
        await Ok(Put("Vorlage/Unter/x.txt", "x"));
        var before = (await Service.GetLatestRevisionAsync(SpaceId, Ct)).Value!.Latest;

        await Ok(new CommitRequest(CommitOps.Copy, "Vorlage", 0, NewPath: "Projekt"));

        var snapshot = (await Service.GetSnapshotAsync(SpaceId, "Projekt", Ct)).Value!;
        var feed = (await Service.GetChangesAsync(SpaceId, before, 100, Ct)).Value!.Changes;
        Assert.Equal(["Projekt/Unter", "Projekt/Unter/x.txt"], snapshot.Select(e => e.Path));
        Assert.Equal(["Projekt", "Projekt/Unter", "Projekt/Unter/x.txt"], feed.Select(c => c.Path));
        Assert.All(feed, c => Assert.Equal(ChangeOps.Upsert, c.Op));
    }

    [Fact]
    public async Task Copy_is_rejected_onto_an_existing_node_or_into_itself()
    {
        await Ok(Mkdir("A"));
        await Ok(Mkdir("B"));

        Assert.Equal(ErrorCode.AlreadyExists, (await Rejected(new CommitRequest(CommitOps.Copy, "A", 0, NewPath: "B"))).Code);
        Assert.Equal(ErrorCode.InvalidPath, (await Rejected(new CommitRequest(CommitOps.Copy, "A", 0, NewPath: "A/Kopie"))).Code);
    }

    // ---------- Papierkorb ----------

    [Fact]
    public async Task The_trash_lists_top_level_deletions_with_origin_and_time()
    {
        await Ok(Mkdir("Ordner"));
        var inner = await Ok(Put("Ordner/innen.txt", "i"));
        var file = await Ok(Put("lose.txt", "l"));
        await Ok(Delete("lose.txt", file.Revision));
        Clock.Advance(TimeSpan.FromHours(1));
        await Ok(Delete("Ordner", inner.Revision));

        var trash = (await Service.ListTrashAsync(SpaceId, Ct)).Value!.Entries;

        Assert.Equal(["Ordner", "lose.txt"], trash.Select(e => e.OriginalPath)); // neueste zuerst, ohne Kinder
        Assert.Equal(Clock.GetUtcNow(), trash[0].DeletedAt);
        Assert.Equal(NodeKinds.Directory, trash[0].Kind);
    }

    [Fact]
    public async Task Restoring_a_deleted_folder_brings_back_its_content_and_announces_it()
    {
        var dir = await Ok(Mkdir("Ordner"));
        var inner = await Ok(Put("Ordner/innen.txt", "i"));
        await Ok(Delete("Ordner", inner.Revision));
        var nodeId = (await Service.ListTrashAsync(SpaceId, Ct)).Value!.Entries.Single().NodeId;
        var before = (await Service.GetLatestRevisionAsync(SpaceId, Ct)).Value!.Latest;

        var restored = await Service.RestoreFromTrashAsync(SpaceId, nodeId, Anna, Ct);

        Assert.True(restored.IsSuccess);
        Assert.Equal(dir.NodeId, restored.Value!.NodeId);
        Assert.Equal(["Ordner/innen.txt"], (await Service.GetSnapshotAsync(SpaceId, "Ordner", Ct)).Value!.Select(e => e.Path));
        Assert.Equal(["Ordner", "Ordner/innen.txt"], (await Service.GetChangesAsync(SpaceId, before, 10, Ct)).Value!.Changes.Select(c => c.Path));
        Assert.Empty((await Service.ListTrashAsync(SpaceId, Ct)).Value!.Entries);
    }

    [Fact]
    public async Task A_file_whose_folder_is_also_deleted_cannot_be_restored_alone()
    {
        await Ok(Mkdir("Ordner"));
        var file = await Ok(Put("Ordner/x.txt", "x"));
        await Ok(Delete("Ordner/x.txt", file.Revision));
        var fileId = file.NodeId;
        var dir = (await Service.GetNodeAsync(SpaceId, "Ordner", Ct)).Value!.Node;
        await Ok(Delete("Ordner", dir.Revision));

        Assert.Equal(ErrorCode.ParentMissing, (await Service.RestoreFromTrashAsync(SpaceId, fileId, Anna, Ct)).Error!.Code);
        Assert.Equal(ErrorCode.NotFound, (await Service.RestoreFromTrashAsync(SpaceId, "n_unbekannt", Anna, Ct)).Error!.Code);
    }

    // ---------- Sperren ----------

    [Fact]
    public async Task A_locked_file_can_only_be_changed_moved_or_deleted_by_whoever_locked_it()
    {
        var created = await OkAs(Anna, Put("vertrag.docx", "v1"));

        var locked = await Service.LockAsync(SpaceId, "vertrag.docx", Anna, Ct);

        Assert.Equal(Anna, locked.Value!.LockedBy);
        Assert.Equal(created.Revision, locked.Value.Revision); // Sperren ändert die Revision nicht
        Assert.Equal(ErrorCode.Locked, (await RejectedAs(Ben, Put("vertrag.docx", "von Ben", created.Revision))).Code);
        Assert.Equal(ErrorCode.Locked, (await RejectedAs(Ben, Delete("vertrag.docx", created.Revision))).Code);
        Assert.Equal(ErrorCode.Locked, (await RejectedAs(Ben, Move("vertrag.docx", "weg.docx", created.Revision))).Code);
        await OkAs(Ben, new CommitRequest(CommitOps.Copy, "vertrag.docx", 0, NewPath: "Kopie.docx")); // Kopieren bleibt erlaubt
        var edited = await OkAs(Anna, Put("vertrag.docx", "v2 von Anna", created.Revision));
        Assert.Equal(Anna, (await Service.GetNodeAsync(SpaceId, "vertrag.docx", Ct)).Value!.Node.LockedBy); // Sperre bleibt nach Bearbeiten
        Assert.Null((await Service.GetNodeAsync(SpaceId, "Kopie.docx", Ct)).Value!.Node.LockedBy);
        Assert.True(edited.Revision > created.Revision);
    }

    [Fact]
    public async Task A_folder_containing_a_foreign_lock_cannot_be_deleted()
    {
        await OkAs(Anna, Mkdir("Team"));
        await OkAs(Anna, Put("Team/plan.xlsx", "p"));
        await Service.LockAsync(SpaceId, "Team/plan.xlsx", Anna, Ct);
        var dir = (await Service.GetNodeAsync(SpaceId, "Team", Ct)).Value!.Node;

        var error = await RejectedAs(Ben, Delete("Team", dir.Revision));

        Assert.Equal(ErrorCode.Locked, error.Code);
        Assert.Equal(Anna, error.Current!.LockedBy);
    }

    [Fact]
    public async Task Only_the_locker_or_a_manager_can_unlock_and_others_cannot_lock_on_top()
    {
        await OkAs(Anna, Put("x.txt", "x"));
        await Service.LockAsync(SpaceId, "x.txt", Anna, Ct);

        Assert.Equal(ErrorCode.Locked, (await Service.LockAsync(SpaceId, "x.txt", Ben, Ct)).Error!.Code);
        Assert.Equal(ErrorCode.Forbidden, (await Service.UnlockAsync(SpaceId, "x.txt", Ben, canOverride: false, Ct)).Error!.Code);
        Assert.Null((await Service.UnlockAsync(SpaceId, "x.txt", Ben, canOverride: true, Ct)).Value!.LockedBy);
        await OkAs(Ben, Put("x.txt", "jetzt Ben", (await Service.GetNodeAsync(SpaceId, "x.txt", Ct)).Value!.Node.Revision));
    }

    [Fact]
    public async Task Locking_is_announced_in_the_feed_without_touching_content_and_folders_cannot_be_locked()
    {
        await OkAs(Anna, Mkdir("Ordner"));
        await OkAs(Anna, Put("Ordner/x.txt", "x"));
        var before = (await Service.GetLatestRevisionAsync(SpaceId, Ct)).Value!.Latest;

        await Service.LockAsync(SpaceId, "Ordner/x.txt", Anna, Ct);
        await Service.LockAsync(SpaceId, "Ordner/x.txt", Anna, Ct); // zweimal sperren: keine zweite Meldung

        var feed = (await Service.GetChangesAsync(SpaceId, before, 10, Ct)).Value!.Changes;
        Assert.Equal((ChangeOps.Lock, "Ordner/x.txt"), (Assert.Single(feed).Op, feed[0].Path));
        Assert.Equal(ErrorCode.InvalidRequest, (await Service.LockAsync(SpaceId, "Ordner", Anna, Ct)).Error!.Code);
    }

    [Fact]
    public async Task Deleting_a_locked_file_by_its_locker_clears_the_lock()
    {
        var created = await OkAs(Anna, Put("x.txt", "x"));
        await Service.LockAsync(SpaceId, "x.txt", Anna, Ct);

        await OkAs(Anna, Delete("x.txt", created.Revision));
        var recreated = await OkAs(Ben, Put("x.txt", "neu", (await Service.GetNodeAsync(SpaceId, "x.txt", Ct)).Value!.Node.Revision));

        Assert.True(recreated.Revision > created.Revision);
        Assert.Null((await Service.GetNodeAsync(SpaceId, "x.txt", Ct)).Value!.Node.LockedBy);
    }
}
