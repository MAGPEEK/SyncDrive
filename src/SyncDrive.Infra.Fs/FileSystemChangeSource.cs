using System.Threading.Channels;
using SyncDrive.Client.App.Ports;
using SyncDrive.Core.Paths;

namespace SyncDrive.Infra.Fs;

/// <summary>
/// <see cref="ILocalChangeSource"/> über <see cref="FileSystemWatcher"/>. Der Watcher kann Ereignisse verlieren; dann kommt
/// <see cref="LocalChange.Overflow"/>, und der periodische Rescan bleibt ohnehin das Sicherheitsnetz (Risiko-Tabelle in 05-test-und-qualitaet.md).
/// Der interne Ordner und nicht darstellbare Namen werden ignoriert.
/// </summary>
public sealed class FileSystemChangeSource : ILocalChangeSource
{
    private const int WatcherBufferBytes = 64 * 1024;

    private readonly string _root;
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<LocalChange> _channel = Channel.CreateUnbounded<LocalChange>(new UnboundedChannelOptions { SingleReader = true });

    public FileSystemChangeSource(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = WatcherBufferBytes,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) =>
        {
            Report(e.FullPath);
            ReportExistingChildren(e.FullPath);
        };
        _watcher.Changed += (_, e) => Report(e.FullPath);
        _watcher.Deleted += (_, e) => Report(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            if (ToPath(e.OldFullPath) is { } from && ToPath(e.FullPath) is { } to)
            {
                _channel.Writer.TryWrite(new LocalChange.Moved(from, to));
            }
            else
            {
                Report(e.OldFullPath); // aus dem/in den internen Ordner oder ungültiger Name: einzeln bewerten
                Report(e.FullPath);
            }
        };
        _watcher.Error += (_, _) => _channel.Writer.TryWrite(new LocalChange.Overflow());
        _watcher.EnableRaisingEvents = true;
    }

    public IAsyncEnumerable<LocalChange> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public void Dispose()
    {
        _watcher.Dispose();
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Ein neuer Ordner kann schon Inhalt haben, bevor er beobachtet wird (unter Linux/inotify kommt die Überwachung eines neuen Unterordners
    /// erst nach dessen Anlegen; ebenso bei einem hineinverschobenen Ordner). Diese Einträge werden einmal nachgemeldet.
    /// </summary>
    private void ReportExistingChildren(string fullPath)
    {
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories))
            {
                Report(entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _channel.Writer.TryWrite(new LocalChange.Overflow()); // nicht lesbar: der Rescan holt es nach
        }
    }

    private void Report(string fullPath)
    {
        if (ToPath(fullPath) is { } path)
        {
            _channel.Writer.TryWrite(new LocalChange.PathChanged(path));
        }
    }

    /// <summary>Relativer Pfad oder null, wenn er im internen Ordner liegt, die Wurzel ist oder nicht darstellbar ist.</summary>
    private RelativePath? ToPath(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith(LocalFileStore.InternalFolder, StringComparison.OrdinalIgnoreCase)
            && (relative.Length == LocalFileStore.InternalFolder.Length || relative[LocalFileStore.InternalFolder.Length] == '/'))
        {
            return null;
        }

        return RelativePath.TryParse(relative, out var path) && !path.IsRoot ? path : null;
    }
}
