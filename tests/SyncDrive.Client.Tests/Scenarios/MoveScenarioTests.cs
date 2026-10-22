using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Contracts;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.Tests.Scenarios;

/// <summary>Lokale Umbenennungen werden zu Server-Verschiebungen (Szenario 6 aus 05-test-und-qualitaet.md): keine Neuübertragung, nichts im Papierkorb.</summary>
public sealed class MoveScenarioTests : IDisposable
{
    private readonly Cluster _cluster = new();

    public void Dispose() => _cluster.Dispose();

    private static int Downloads(TestClient c) => c.Log.Entries.Count(e => e.Action == ActivityAction.Download);

    [Fact]
    public async Task Renaming_a_folder_offline_becomes_a_single_server_move_that_others_follow()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        for (var i = 0; i < 30; i++)
        {
            a.Write($"Projekt Alt/Unterordner/Datei{i}.txt", $"Inhalt {i}");
        }

        await a.SyncOkAsync();
        await b.StartOkAsync();
        var downloadsBefore = Downloads(b);
        var commitsBefore = a.Remote.Commits.Count;

        Directory.Move(a.Full("Projekt Alt"), a.Full("Projekt Neu"));
        await a.SyncOkAsync();
        await b.PullAsync();

        var commits = a.Remote.Commits.Skip(commitsBefore).ToList();
        var move = Assert.Single(commits);
        Assert.Equal((CommitOps.Move, "Projekt Alt", "Projekt Neu"), (move.Op, move.Path, move.NewPath));
        Assert.Equal(30, b.Files().Keys.Count(k => k.StartsWith("Projekt Neu/", StringComparison.Ordinal)));
        Assert.False(b.Exists("Projekt Alt"));
        Assert.Equal(downloadsBefore, Downloads(b));
        Assert.Empty(b.TrashFiles());
    }

    [Fact]
    public async Task Renaming_a_file_offline_becomes_a_move()
    {
        var a = _cluster.AddClient("A");
        a.Write("Entwurf.docx", "Text");
        await a.SyncOkAsync();
        var before = a.Remote.Commits.Count;
        var blocksBefore = a.Remote.UploadedBlocks.Count;

        File.Move(a.Full("Entwurf.docx"), a.Full("Final.docx"));
        await a.SyncOkAsync();

        var move = Assert.Single(a.Remote.Commits.Skip(before));
        Assert.Equal((CommitOps.Move, "Entwurf.docx", "Final.docx"), (move.Op, move.Path, move.NewPath));
        Assert.Equal(blocksBefore, a.Remote.UploadedBlocks.Count);
    }

    [Fact]
    public async Task Moving_a_file_into_another_folder_is_found_by_content()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("Eingang/Rechnung.pdf", "PDF-Inhalt");
        Directory.CreateDirectory(a.Full("Archiv"));
        await a.SyncOkAsync();
        await b.SyncOkAsync();
        var before = a.Remote.Commits.Count;

        File.Move(a.Full("Eingang/Rechnung.pdf"), a.Full("Archiv/Rechnung.pdf"));
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        var move = Assert.Single(a.Remote.Commits.Skip(before));
        Assert.Equal((CommitOps.Move, "Eingang/Rechnung.pdf", "Archiv/Rechnung.pdf"), (move.Op, move.Path, move.NewPath));
        Assert.Equal("PDF-Inhalt", b.Read("Archiv/Rechnung.pdf"));
        Assert.False(b.Exists("Eingang/Rechnung.pdf"));
    }

    [Fact]
    public async Task Rename_plus_edit_still_ends_correctly_on_the_other_client()
    {
        var a = _cluster.AddClient("A");
        var b = _cluster.AddClient("B");
        a.Write("alt.txt", "v1");
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        File.Move(a.Full("alt.txt"), a.Full("neu.txt"));
        a.Write("neu.txt", "v2 mit mehr Inhalt");
        await a.SyncOkAsync();
        await b.SyncOkAsync();

        Assert.Equal("v2 mit mehr Inhalt", b.Read("neu.txt"));
        Assert.False(b.Exists("alt.txt"));
    }

    [Fact]
    public async Task Ambiguous_candidates_are_not_guessed()
    {
        var a = _cluster.AddClient("A");
        a.Write("a.txt", "gleich");
        a.Write("b.txt", "gleich");
        await a.SyncOkAsync();
        var before = a.Remote.Commits.Count;

        File.Move(a.Full("a.txt"), a.Full("x.txt"));
        File.Move(a.Full("b.txt"), a.Full("y.txt"));
        await a.SyncOkAsync();

        Assert.DoesNotContain(a.Remote.Commits.Skip(before), c => c.Op == CommitOps.Move);
        Assert.Equal(["x.txt", "y.txt"], (await _cluster.Service.GetSnapshotAsync(Cluster.Space, string.Empty, CancellationToken.None)).Value!.Select(e => e.Path));
    }

    [Fact]
    public async Task A_watched_move_event_is_applied_directly()
    {
        var a = _cluster.AddClient("A");
        a.Write("Ordner/x.txt", "x");
        await a.SyncOkAsync();
        var before = a.Remote.Commits.Count;

        Directory.Move(a.Full("Ordner"), a.Full("Umbenannt"));
        var result = await a.Session.ApplyLocalMoveAsync(RelativePath.Parse("Ordner"), RelativePath.Parse("Umbenannt"), evaluateAfter: true, CancellationToken.None);

        Assert.Equal(OutcomeKind.Moved, result.Value!.Kind);
        Assert.Equal(CommitOps.Move, Assert.Single(a.Remote.Commits.Skip(before)).Op);
        Assert.Contains(a.Log.Entries, e => e.Action == ActivityAction.Move);
    }
}
