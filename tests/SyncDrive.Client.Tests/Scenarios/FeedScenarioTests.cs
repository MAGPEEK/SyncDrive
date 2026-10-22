using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Contracts;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>Server-Änderungen über den Feed statt über Rescans (A6, ADR-3).</summary>
public sealed class FeedScenarioTests : IDisposable
{
    private readonly Cluster _cluster = new();

    public void Dispose() => _cluster.Dispose();

    [Fact]
    public async Task Changes_from_another_client_arrive_through_the_feed()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        await b.StartOkAsync();

        a.Write("Ordner/neu.txt", "über den Feed");
        await a.SyncOkAsync();
        var pulled = await b.PullAsync();

        Assert.True(pulled.IsSuccess);
        Assert.Equal(2, pulled.Value);
        Assert.Equal("über den Feed", b.Read("Ordner/neu.txt"));
        Assert.Equal(0, (await b.PullAsync()).Value); // nichts Neues
    }

    [Fact]
    public async Task A_remote_delete_arrives_through_the_feed_and_goes_to_the_trash()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("x.txt", "weg damit");
        await a.SyncOkAsync();
        await b.StartOkAsync();

        a.Delete("x.txt");
        await a.SyncOkAsync();
        await b.PullAsync();

        Assert.False(b.Exists("x.txt"));
        Assert.Contains("weg damit", b.TrashFiles());
    }

    [Fact]
    public async Task A_remote_rename_of_a_folder_is_followed_locally_without_downloading()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        for (var i = 0; i < 20; i++)
        {
            a.Write($"Alt/Datei{i}.txt", $"Inhalt {i}");
        }

        await a.SyncOkAsync();
        await b.StartOkAsync();
        var downloadsBefore = b.Log.Entries.Count(e => e.Action == ActivityAction.Download);
        var revision = (await _cluster.Service.GetNodeAsync(Cluster.Space, "Alt", CancellationToken.None)).Value!.Node.Revision;

        await _cluster.Service.CommitAsync(Cluster.Space, new CommitRequest(CommitOps.Move, "Alt", revision, NewPath: "Neu"), null, CancellationToken.None);
        var pulled = await b.PullAsync();

        Assert.True(pulled.IsSuccess);
        Assert.False(b.Exists("Alt"));
        Assert.Equal(20, b.Files().Keys.Count(k => k.StartsWith("Neu/", StringComparison.Ordinal)));
        Assert.Equal(downloadsBefore, b.Log.Entries.Count(e => e.Action == ActivityAction.Download));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(b.Root, ".syncdrive", "trash")));
        Assert.NotNull(await b.State.GetAsync(Core.Paths.RelativePath.Parse("Neu/Datei7.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_cursor_leads_to_a_full_resync()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        await b.StartOkAsync();
        a.Write("verpasst.txt", "trotzdem da");
        await a.SyncOkAsync();
        var latest = (await _cluster.Service.GetLatestRevisionAsync(Cluster.Space, CancellationToken.None)).Value!.Latest;
        _cluster.Store.Floor = latest; // Feed wurde gekürzt

        var pulled = await b.PullAsync();

        Assert.True(pulled.IsSuccess);
        Assert.Equal("trotzdem da", b.Read("verpasst.txt"));
        Assert.Equal(latest, await b.State.GetFeedCursorAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Without_network_the_cursor_stays_and_nothing_is_lost()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        await b.StartOkAsync();
        var cursor = await b.State.GetFeedCursorAsync(CancellationToken.None);
        a.Write("später.txt", "kommt noch");
        await a.SyncOkAsync();

        b.Remote.Offline = true;
        var failed = await b.PullAsync();
        b.Remote.Offline = false;

        Assert.Equal(RemoteErrors.Network, failed.Error!.Code);
        Assert.Equal(cursor, await b.State.GetFeedCursorAsync(CancellationToken.None));
        Assert.True((await b.PullAsync()).IsSuccess);
        Assert.Equal("kommt noch", b.Read("später.txt"));
    }

    [Fact]
    public async Task A_long_poll_returns_as_soon_as_another_client_commits()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        await b.StartOkAsync();

        var waiting = b.PullAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        a.Write("sofort.txt", "!");
        await a.SyncOkAsync();
        var pulled = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(pulled.IsSuccess);
        Assert.Equal("!", b.Read("sofort.txt"));
    }

}
