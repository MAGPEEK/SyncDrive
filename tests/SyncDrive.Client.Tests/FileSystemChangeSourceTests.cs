using SyncDrive.Client.App.Ports;
using SyncDrive.Infra.Fs;

namespace SyncDrive.Client.Tests;

public sealed class FileSystemChangeSourceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncdrive-watch-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemChangeSource _source;

    public FileSystemChangeSourceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, LocalFileStore.InternalFolder));
        _source = new FileSystemChangeSource(_root);
    }

    public void Dispose()
    {
        _source.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Sammelt gemeldete Pfade, bis alle erwarteten da sind (oder die Zeit abläuft).</summary>
    private async Task<HashSet<string>> CollectUntilAsync(params string[] expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await foreach (var change in _source.ReadAllAsync(timeout.Token))
            {
                switch (change)
                {
                    case LocalChange.PathChanged changed:
                        seen.Add(changed.Path.Value);
                        break;
                    case LocalChange.Moved moved:
                        seen.Add("move:" + moved.From.Value + "->" + moved.To.Value);
                        break;
                }

                if (expected.All(seen.Contains))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Zeit abgelaufen; die Prüfung unten zeigt, was fehlt
        }

        return seen;
    }

    [Fact]
    public async Task Creating_a_file_in_a_subfolder_is_reported_with_its_relative_path()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Ordner"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Ordner", "neu.txt"), "x");

        var seen = await CollectUntilAsync("Ordner/neu.txt");

        Assert.Contains("Ordner/neu.txt", seen);
    }

    [Fact]
    public async Task A_rename_is_reported_as_one_move_from_old_to_new()
    {
        var old = Path.Combine(_root, "alt.txt");
        await File.WriteAllTextAsync(old, "x");
        await Task.Delay(300);

        File.Move(old, Path.Combine(_root, "neu.txt"));
        var seen = await CollectUntilAsync("move:alt.txt->neu.txt");

        Assert.Contains("move:alt.txt->neu.txt", seen);
    }

    [Fact]
    public async Task Changes_inside_the_internal_folder_are_ignored()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, LocalFileStore.InternalFolder, "intern.tmp"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "sichtbar.txt"), "x");

        var seen = await CollectUntilAsync("sichtbar.txt");

        Assert.Contains("sichtbar.txt", seen);
        Assert.DoesNotContain(seen, p => p.StartsWith(LocalFileStore.InternalFolder, StringComparison.OrdinalIgnoreCase));
    }
}
