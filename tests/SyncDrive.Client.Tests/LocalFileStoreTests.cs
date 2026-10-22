using System.Text;
using SyncDrive.Client.App.Ports;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.Fs;

namespace SyncDrive.Client.Tests;

public sealed class LocalFileStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncdrive-fs-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileStore _store;

    public LocalFileStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new LocalFileStore(_root, new FixedClock(T0));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static RelativePath P(string path) => RelativePath.Parse(path);

    private string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
        File.WriteAllText(Full(relative), content);
    }

    private async Task<List<ScanItem>> Scan(string root = "")
    {
        var items = new List<ScanItem>();
        await foreach (var item in _store.ScanAsync(P(root), Ct))
        {
            items.Add(item);
        }

        return items;
    }

    private static IEnumerable<string> Paths(IEnumerable<ScanItem> items) =>
        items.OfType<ScanItem.Found>().Select(f => f.Entry.Path.Value);

    [Fact]
    public async Task Scan_lists_files_and_folders_with_size_and_mtime()
    {
        Write("a/b/file.txt", "12345");
        File.SetLastWriteTimeUtc(Full("a/b/file.txt"), T0.UtcDateTime);

        var items = await Scan();

        Assert.Equal(["a", "a/b", "a/b/file.txt"], Paths(items).Order(StringComparer.Ordinal));
        var file = items.OfType<ScanItem.Found>().Single(f => f.Entry.Kind == NodeKind.File).Entry;
        Assert.Equal((5L, T0), (file.Size, file.MTime));
    }

    [Fact]
    public async Task Scan_never_shows_the_internal_folder()
    {
        Write("visible.txt");

        var items = await Scan();

        Assert.Equal(["visible.txt"], Paths(items));
        Assert.DoesNotContain(items, i => i is ScanItem.Skipped);
    }

    [Fact]
    public async Task Scan_of_a_subtree_only_returns_that_subtree()
    {
        Write("a/one.txt");
        Write("b/two.txt");

        Assert.Equal(["a/one.txt"], Paths(await Scan("a")));
    }

    [Fact]
    public async Task Scan_of_a_missing_folder_is_empty()
    {
        Assert.Empty(await Scan("nope"));
    }

    [Fact]
    public async Task Scan_reports_names_it_cannot_sync_instead_of_hiding_them()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // unter Windows lassen sich solche Namen nicht anlegen
        }

        Write("fine.txt");
        Write("bad:name.txt");

        var items = await Scan();

        Assert.Equal(["fine.txt"], Paths(items));
        Assert.Contains(items, i => i is ScanItem.Skipped { Reason: SkipReason.InvalidName });
    }

    [Fact]
    public async Task Scan_does_not_follow_symbolic_links()
    {
        Write("real/inside.txt");
        try
        {
            Directory.CreateSymbolicLink(Full("link"), Full("real"));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return; // Windows ohne Entwicklermodus darf keine Links anlegen
        }

        var items = await Scan();

        Assert.Contains(items, i => i is ScanItem.Skipped { Reason: SkipReason.Link });
        Assert.DoesNotContain("link/inside.txt", Paths(items));
    }

    [Fact]
    public async Task Stat_returns_null_for_missing_and_for_internal_paths()
    {
        Write("x.txt");

        Assert.NotNull(await _store.StatAsync(P("x.txt"), Ct));
        Assert.Null(await _store.StatAsync(P("missing.txt"), Ct));
        Assert.Null(await _store.StatAsync(P(".syncdrive"), Ct));
    }

    [Fact]
    public async Task Staged_file_is_invisible_until_commit_then_appears_atomically_with_mtime()
    {
        await using var staged = await _store.StageAsync(P("docs/new.txt"), Ct);
        await staged.Content.WriteAsync("Hallo"u8.ToArray(), Ct);

        Assert.False(File.Exists(Full("docs/new.txt")));
        await staged.CommitAsync(T0, Ct);

        Assert.Equal("Hallo", await File.ReadAllTextAsync(Full("docs/new.txt"), Ct));
        Assert.Equal(T0.UtcDateTime, File.GetLastWriteTimeUtc(Full("docs/new.txt")));
    }

    [Fact]
    public async Task Disposing_without_commit_leaves_no_file_and_no_leftovers()
    {
        await using (var staged = await _store.StageAsync(P("never.txt"), Ct))
        {
            await staged.Content.WriteAsync("abgebrochen"u8.ToArray(), Ct);
        }

        Assert.False(File.Exists(Full("never.txt")));
        Assert.Empty(Directory.GetFiles(Full(".syncdrive/staging")));
    }

    [Fact]
    public async Task Commit_replaces_an_existing_file_completely()
    {
        Write("doc.txt", "alt und ziemlich lang");

        await using (var staged = await _store.StageAsync(P("doc.txt"), Ct))
        {
            await staged.Content.WriteAsync("neu"u8.ToArray(), Ct);
            await staged.CommitAsync(T0, Ct);
        }

        Assert.Equal("neu", await File.ReadAllTextAsync(Full("doc.txt"), Ct));
    }

    [Fact]
    public async Task Replacing_an_open_file_never_corrupts_it_and_can_be_retried_without_restaging()
    {
        Write("live.txt", "version 1");
        var reader = await _store.OpenReadAsync(P("live.txt"), Ct);
        await using var staged = await _store.StageAsync(P("live.txt"), Ct);
        await staged.Content.WriteAsync("version 2"u8.ToArray(), Ct);

        try
        {
            await staged.CommitAsync(T0, Ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Windows: Ziel ist gesperrt. Die alte Version bleibt unversehrt, der zweite Versuch gelingt.
            Assert.Equal("version 1", await File.ReadAllTextAsync(Full("live.txt"), Ct));
            await reader.DisposeAsync();
            await staged.CommitAsync(T0, Ct);
        }

        await reader.DisposeAsync();
        Assert.Equal("version 2", await File.ReadAllTextAsync(Full("live.txt"), Ct));
    }

    [Fact]
    public async Task Create_directory_makes_parents_and_sets_mtime()
    {
        await _store.CreateDirectoryAsync(P("a/b/c"), T0, Ct);

        Assert.True(Directory.Exists(Full("a/b/c")));
        Assert.Equal(T0.UtcDateTime, Directory.GetLastWriteTimeUtc(Full("a/b/c")));
    }

    [Fact]
    public async Task Trash_moves_a_file_and_keeps_its_content_and_relative_path()
    {
        Write("docs/old.txt", "wichtig");

        var item = await _store.MoveToTrashAsync(P("docs/old.txt"), Ct);

        Assert.False(File.Exists(Full("docs/old.txt")));
        Assert.Equal("wichtig", await File.ReadAllTextAsync(item!.TrashPath, Ct));
        Assert.StartsWith(Full(".syncdrive/trash"), item.TrashPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("docs", "old.txt"), item.TrashPath, StringComparison.Ordinal);
        Assert.Equal(T0, item.DeletedAt);
    }

    [Fact]
    public async Task Trash_moves_whole_folders()
    {
        Write("dir/a.txt");
        Write("dir/sub/b.txt");

        var item = await _store.MoveToTrashAsync(P("dir"), Ct);

        Assert.False(Directory.Exists(Full("dir")));
        Assert.True(File.Exists(Path.Combine(item!.TrashPath, "sub", "b.txt")));
    }

    [Fact]
    public async Task Trashing_the_same_path_twice_keeps_both_copies()
    {
        Write("x.txt", "eins");
        var first = await _store.MoveToTrashAsync(P("x.txt"), Ct);
        Write("x.txt", "zwei");

        var second = await _store.MoveToTrashAsync(P("x.txt"), Ct);

        Assert.NotEqual(first!.TrashPath, second!.TrashPath);
        Assert.Equal("eins", await File.ReadAllTextAsync(first.TrashPath, Ct));
        Assert.Equal("zwei", await File.ReadAllTextAsync(second.TrashPath, Ct));
    }

    [Fact]
    public async Task Trashing_something_that_does_not_exist_is_a_noop()
    {
        Assert.Null(await _store.MoveToTrashAsync(P("ghost.txt"), Ct));
    }

    [Fact]
    public async Task Trash_content_never_appears_in_a_scan()
    {
        Write("x.txt");
        await _store.MoveToTrashAsync(P("x.txt"), Ct);

        Assert.Empty(await Scan());
    }

    [Fact]
    public async Task Move_renames_creating_the_target_folder_and_keeps_content()
    {
        Write("a.txt", "inhalt");

        await _store.MoveAsync(P("a.txt"), P("neu/b.txt"), Ct);

        Assert.False(File.Exists(Full("a.txt")));
        Assert.Equal("inhalt", await File.ReadAllTextAsync(Full("neu/b.txt"), Ct));
    }

    [Fact]
    public async Task Move_never_overwrites_an_existing_target()
    {
        Write("a.txt", "a");
        Write("b.txt", "b");

        await Assert.ThrowsAsync<IOException>(() => _store.MoveAsync(P("a.txt"), P("b.txt"), Ct));

        Assert.Equal("b", await File.ReadAllTextAsync(Full("b.txt"), Ct));
        Assert.True(File.Exists(Full("a.txt")));
    }

    [Fact]
    public async Task Move_supports_case_only_rename()
    {
        Write("report.txt");

        await _store.MoveAsync(P("report.txt"), P("Report.txt"), Ct);

        Assert.Contains(Directory.GetFiles(_root), f => Path.GetFileName(f) == "Report.txt");
    }

    [Fact]
    public async Task Moves_whole_folders()
    {
        Write("old/inner/f.txt", "f");

        await _store.MoveAsync(P("old"), P("new"), Ct);

        Assert.Equal("f", await File.ReadAllTextAsync(Full("new/inner/f.txt"), Ct));
    }

    [Fact]
    public async Task Utf8_names_with_umlauts_round_trip()
    {
        Write("Übergrößen/Straße.txt", Encoding.UTF8.GetString("ä"u8));

        Assert.Contains("Übergrößen/Straße.txt", Paths(await Scan()));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
