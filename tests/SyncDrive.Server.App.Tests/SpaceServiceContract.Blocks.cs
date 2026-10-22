using System.Text;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Tests.Fakes;

namespace SyncDrive.Server.App.Tests;

/// <summary>Blöcke je Space (M7, 0.15.0): fremde Blöcke sind unsichtbar und lassen sich nicht ohne eigenes Hochladen verwenden.</summary>
public abstract partial class SpaceServiceContract
{
    private const string OtherSpace = "s2";
    private const string Eve = "u_eve";

    private static MemoryStream Bytes(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task A_block_of_another_space_is_neither_reported_present_nor_handed_out()
    {
        _fixture.AddSpace(OtherSpace);
        var put = Put("geheim.txt", "nur in s1");
        await Ok(put);
        var block = put.Blocks![0];

        var missingThere = await Service.MissingBlocksAsync(OtherSpace, [block], Eve, Ct);
        var openThere = await Service.OpenBlockAsync(OtherSpace, block, Ct);
        var openHere = await Service.OpenBlockAsync(SpaceId, block, Ct);

        Assert.Equal([block], missingThere.Value!.Missing);
        Assert.Equal(ErrorCode.NotFound, openThere.Error!.Code);
        Assert.True(openHere.IsSuccess);
        await openHere.Value!.DisposeAsync();
    }

    [Fact]
    public async Task A_commit_cannot_use_a_foreign_block_until_the_actor_uploads_it_itself()
    {
        _fixture.AddSpace(OtherSpace);
        var put = Put("geheim.txt", "nur in s1");
        await Ok(put);

        var stolen = await Service.CommitAsync(OtherSpace, put, null, Eve, Ct);
        await Service.PutBlockAsync(put.Blocks![0], Bytes("nur in s1"), Eve, Ct);
        var ownCopy = await Service.CommitAsync(OtherSpace, put, null, Eve, Ct);

        Assert.Equal(ErrorCode.BlocksMissing, stolen.Error!.Code);
        Assert.Equal([put.Blocks[0]], stolen.Error.MissingBlocks!);
        Assert.True(ownCopy.IsSuccess);
    }

    [Fact]
    public async Task An_upload_counts_as_present_only_for_whoever_uploaded_it()
    {
        _fixture.AddSpace(OtherSpace);
        var hash = Fixtures.HashOf(Encoding.UTF8.GetBytes("neu")).Value;

        await Service.PutBlockAsync(hash, Bytes("neu"), Eve, Ct);
        var forEve = await Service.MissingBlocksAsync(OtherSpace, [hash], Eve, Ct);
        var forBen = await Service.MissingBlocksAsync(OtherSpace, [hash], Ben, Ct);

        Assert.Empty(forEve.Value!.Missing);
        Assert.Equal([hash], forBen.Value!.Missing);
    }

    [Fact]
    public async Task Blocks_of_older_versions_and_deleted_files_stay_available_in_their_space()
    {
        var first = Put("doc.txt", "erste Fassung");
        var v1 = await Ok(first);
        var v2 = await Ok(Put("doc.txt", "zweite Fassung", v1.Revision));
        await Ok(Delete("doc.txt", v2.Revision));

        var old = await Service.OpenBlockAsync(SpaceId, first.Blocks![0], Ct);

        Assert.True(old.IsSuccess);
        await old.Value!.DisposeAsync();
    }
}
