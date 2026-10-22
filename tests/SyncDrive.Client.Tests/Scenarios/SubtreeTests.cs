using SyncDrive.Core.Paths;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>Eine Aufgabe gleicht nur einen Ordner des Space ab (im Assistenten gewählt); A sieht den ganzen Space, B nur „Projekt“.</summary>
public sealed class SubtreeTests : IDisposable
{
    private static readonly RelativePath Projekt = RelativePath.Parse("Projekt");
    private readonly Cluster _cluster = new();

    public void Dispose() => _cluster.Dispose();

    private async Task<(TestClient Whole, TestClient Folder)> SetUpAsync()
    {
        var whole = _cluster.AddClient("A");
        whole.Write("Projekt/plan.txt", "plan");
        whole.Write("Privat/geheim.txt", "nicht für B");
        await whole.SyncOkAsync();
        await whole.StartOkAsync();
        var folder = _cluster.AddClient("B", remoteRoot: Projekt);
        await folder.StartOkAsync();
        return (whole, folder);
    }

    [Fact]
    public async Task Only_the_chosen_folder_arrives_and_its_content_sits_at_the_local_root()
    {
        var (_, b) = await SetUpAsync();

        Assert.Equal(new Dictionary<string, string> { ["plan.txt"] = "plan" }, b.Files());
    }

    [Fact]
    public async Task Local_changes_land_inside_the_folder_on_the_server()
    {
        var (a, b) = await SetUpAsync();

        b.Write("neu/notiz.txt", "von B");
        await b.SyncOkAsync();
        Assert.True((await a.PullAsync()).IsSuccess);

        Assert.Equal("von B", a.Read("Projekt/neu/notiz.txt"));
        Assert.All(a.Remote.Commits.Concat(b.Remote.Commits), c => Assert.False(c.Path.StartsWith("neu", StringComparison.Ordinal), c.Path));
    }

    [Fact]
    public async Task The_feed_only_brings_changes_inside_the_folder()
    {
        var (a, b) = await SetUpAsync();

        a.Write("Privat/mehr.txt", "auch nicht für B");
        a.Write("Projekt/plan.txt", "plan v2");
        await a.SyncOkAsync();
        Assert.True((await b.PullAsync()).IsSuccess);

        Assert.Equal(new Dictionary<string, string> { ["plan.txt"] = "plan v2" }, b.Files());
    }

    [Fact]
    public async Task Moving_out_of_the_folder_deletes_and_moving_in_brings_the_whole_folder()
    {
        var (a, b) = await SetUpAsync();
        a.Write("Projekt/raus/weg.txt", "geht");
        await a.SyncOkAsync();
        Assert.True((await b.PullAsync()).IsSuccess);
        Assert.True(b.Exists("raus/weg.txt"));

        Directory.Move(a.Full("Projekt/raus"), a.Full("Privat/raus"));
        Directory.Move(a.Full("Privat"), a.Full("Projekt/Privat"));
        await a.SyncOkAsync();
        Assert.True((await b.PullAsync()).IsSuccess);

        Assert.False(b.Exists("raus"));
        Assert.Equal("nicht für B", b.Read("Privat/geheim.txt"));
        Assert.Equal("geht", b.Read("Privat/raus/weg.txt"));
    }
}
