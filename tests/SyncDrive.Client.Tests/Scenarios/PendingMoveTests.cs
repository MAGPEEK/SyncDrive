using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>
/// Fund aus dem flackernden Umbenennungs-Szenario: Zwischen Watcher-Ereignis und Übertragung der Verschiebung bewertete der Feed (Echo der
/// eigenen Uploads) den Quellordner, sah ihn lokal fehlen und löschte ihn auf dem Server. Jetzt warten alle Bewertungen auf angemeldete Verschiebungen.
/// </summary>
public sealed class PendingMoveTests : IDisposable
{
    private static readonly RelativePath Alt = RelativePath.Parse("Alt");
    private static readonly RelativePath Neu = RelativePath.Parse("Neu");
    private readonly Cluster _cluster = new();

    public void Dispose() => _cluster.Dispose();

    [Fact]
    public async Task An_evaluation_of_the_source_waits_for_the_announced_move_instead_of_deleting_it()
    {
        var a = _cluster.AddClient("A");
        a.Write("Alt/eins.txt", "1");
        a.Write("Alt/zwei.txt", "2");
        await a.SyncOkAsync();

        Directory.Move(a.Full("Alt"), a.Full("Neu"));
        var announced = a.Session.Moves.Announce(Alt, Neu);
        var feedLooksAtSource = a.Session.EvaluateWithRetryAsync(Alt, startup: false, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(feedLooksAtSource.IsCompleted, "Bewertung der Quelle muss auf die Verschiebung warten");

        var moved = await a.Session.ApplyLocalMoveAsync(Alt, Neu, evaluateAfter: true, CancellationToken.None);
        announced.Dispose();
        await feedLooksAtSource.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(OutcomeKind.Moved, moved.Value!.Kind);
        Assert.DoesNotContain(a.Remote.Commits, c => c.Op == Contracts.CommitOps.Delete);
        Assert.Contains(a.Remote.Commits, c => c.Op == Contracts.CommitOps.Move);
    }

    [Fact]
    public async Task Feed_echoes_of_own_changes_do_not_turn_a_just_renamed_folder_into_a_delete()
    {
        var a = _cluster.AddClient("A");
        a.Write("Alt/eins.txt", "1");
        await a.SyncOkAsync();

        Directory.Move(a.Full("Alt"), a.Full("Neu")); // Watcher-Ereignis noch nicht verarbeitet
        var pulled = await a.PullAsync();

        Assert.True(pulled.IsSuccess);
        Assert.DoesNotContain(a.Remote.Commits, c => c.Op == Contracts.CommitOps.Delete);
    }

    [Fact]
    public async Task A_watcher_echo_of_our_own_download_does_not_act_on_a_server_move_the_feed_has_not_delivered_yet()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("Alt/eins.txt", "1");
        await a.SyncOkAsync();
        await b.SyncOkAsync();
        Directory.Move(a.Full("Alt"), a.Full("Neu"));
        await a.SyncOkAsync(); // Server-Verschiebung

        var echo = await b.Session.EvaluateLocalChangeAsync(Alt, CancellationToken.None);
        Assert.Equal(OutcomeKind.Unchanged, echo.Value!.Kind);
        Assert.True(b.Exists("Alt/eins.txt"));

        var downloads = b.Log.Entries.Count(e => e.Action == App.Ports.ActivityAction.Download);
        Assert.True((await b.PullAsync()).IsSuccess);
        Assert.Equal("1", b.Read("Neu/eins.txt"));
        Assert.Equal(downloads, b.Log.Entries.Count(e => e.Action == App.Ports.ActivityAction.Download));
    }

    [Fact]
    public async Task When_a_server_folder_move_cannot_be_followed_its_content_still_arrives()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("Alt/eins.txt", "1");
        a.Write("Alt/tief/zwei.txt", "2");
        await a.SyncOkAsync();
        await b.SyncOkAsync();
        Directory.Move(a.Full("Alt"), a.Full("Neu"));
        await a.SyncOkAsync();
        Directory.CreateDirectory(b.Full("Neu")); // Ziel lokal belegt: B kann nicht einfach mitverschieben

        Assert.True((await b.PullAsync()).IsSuccess);

        Assert.Equal("1", b.Read("Neu/eins.txt"));
        Assert.Equal("2", b.Read("Neu/tief/zwei.txt"));
        Assert.False(b.Exists("Alt"));
    }

    [Theory]
    [InlineData("Alt", true)]
    [InlineData("Alt/eins.txt", true)]
    [InlineData("Neu", true)]
    [InlineData("", true)]                 // Elternteil der Verschiebung
    [InlineData("Anderes", false)]
    [InlineData("Alter/datei.txt", false)] // nur gleicher Präfix
    public async Task Paths_touching_a_move_wait_and_others_do_not(string path, bool waits)
    {
        var moves = new PendingMoves();
        using var announced = moves.Announce(Alt, Neu);

        var waiting = moves.WaitClearAsync(RelativePath.Parse(path), CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(!waits, waiting.IsCompleted);

        moves.Complete(Alt, Neu);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
