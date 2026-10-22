using System.Globalization;
using System.Runtime.CompilerServices;
using SyncDrive.Client.App.Ports;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Infra.Fs;

/// <summary>
/// <see cref="ILocalStore"/> auf dem echten Dateisystem. Interne Daten (Staging, Papierkorb) liegen im Ordner
/// <c>.syncdrive</c> der Sync-Wurzel: gleiches Laufwerk, daher sind Umbenennungen atomar; der Ordner erscheint nie im Scan.
/// </summary>
public sealed class LocalFileStore : ILocalStore
{
    public const string InternalFolder = ".syncdrive";

    private readonly string _root;
    private readonly string _staging;
    private readonly string _trash;
    private readonly TimeProvider _clock;

    public LocalFileStore(string rootPath, TimeProvider? clock = null)
    {
        _root = Path.GetFullPath(rootPath);
        _staging = Path.Combine(_root, InternalFolder, "staging");
        _trash = Path.Combine(_root, InternalFolder, "trash");
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_trash);
        HideInternalFolder(Path.Combine(_root, InternalFolder));
    }

    /// <summary>Unter Windows unsichtbar wie andere Verwaltungsordner (auch bei bestehenden Sync-Ordnern beim nächsten Start).</summary>
    private static void HideInternalFolder(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // unter Linux und macOS versteckt schon der Punkt am Anfang
        }

        var info = new DirectoryInfo(path);
        if (!info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            info.Attributes |= FileAttributes.Hidden;
        }
    }

    public async IAsyncEnumerable<ScanItem> ScanAsync(
        RelativePath root, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = ToFullPath(root);
        if (!Directory.Exists(start))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var children = TryList(directory);
            if (children is null)
            {
                yield return new ScanItem.Skipped(RawRelative(directory), SkipReason.Unreadable);
                continue;
            }

            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                if (IsInternal(child.FullName))
                {
                    continue;
                }

                var item = Classify(child);
                if (item is ScanItem.Found { Entry.Kind: NodeKind.Directory })
                {
                    pending.Push(child.FullName);
                }

                yield return item;
            }

            await Task.Yield(); // Verzeichnis für Verzeichnis abgeben, damit große Bäume andere Arbeit nicht aushungern
        }
    }

    public Task<LocalEntry?> StatAsync(RelativePath path, CancellationToken ct)
    {
        var full = ToFullPath(path);
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        return Task.FromResult(info.Exists && !IsInternal(full) && Classify(info) is ScanItem.Found found ? found.Entry : null);
    }

    public Task<Stream> OpenReadAsync(RelativePath path, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(
            ToFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));

    public Task<IStagedFile> StageAsync(RelativePath target, CancellationToken ct)
    {
        var temp = Path.Combine(_staging, Guid.NewGuid().ToString("N"));
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        return Task.FromResult<IStagedFile>(new StagedFile(stream, temp, ToFullPath(target)));
    }

    public Task CreateDirectoryAsync(RelativePath path, DateTimeOffset mtime, CancellationToken ct)
    {
        var full = ToFullPath(path);
        Directory.CreateDirectory(full);
        Directory.SetLastWriteTimeUtc(full, mtime.UtcDateTime);
        return Task.CompletedTask;
    }

    public Task<TrashItem?> MoveToTrashAsync(RelativePath path, CancellationToken ct)
    {
        var source = ToFullPath(path);
        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
        {
            return Task.FromResult<TrashItem?>(null);
        }

        var now = _clock.GetUtcNow();
        var batch = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var destination = Path.Combine(_trash, batch, path.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }

        return Task.FromResult<TrashItem?>(new TrashItem(destination, now));
    }

    public Task<int> CleanTrashAsync(TimeSpan maxAge, long maxBytes, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var batches = new DirectoryInfo(_trash).EnumerateDirectories()
            .Select(d => (Dir: d, Time: ParseBatchTime(d.Name), Size: SizeOf(d)))
            .OrderBy(b => b.Time)
            .ToList();
        var total = batches.Sum(b => b.Size);
        var removed = 0;
        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            if (now - batch.Time <= maxAge && total <= maxBytes)
            {
                break; // sortiert: alle folgenden sind jünger, und die Größe passt
            }

            batch.Dir.Delete(recursive: true);
            total -= batch.Size;
            removed++;
        }

        return Task.FromResult(removed);
    }

    private static DateTimeOffset ParseBatchTime(string name) =>
        name.Length >= 15 && DateTimeOffset.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : DateTimeOffset.MinValue; // unbekannter Name: gilt als ältester Stapel

    private static long SizeOf(DirectoryInfo dir) =>
        dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    public Task MoveAsync(RelativePath source, RelativePath target, CancellationToken ct)
    {
        var sourcePath = ToFullPath(source);
        var destination = ToFullPath(target);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            if (!source.EqualsIgnoreCase(target))
            {
                throw new IOException($"Ziel existiert bereits: {target}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destination);
        }
        else
        {
            File.Move(sourcePath, destination);
        }

        return Task.CompletedTask;
    }

    private static List<FileSystemInfo>? TryList(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private ScanItem Classify(FileSystemInfo info)
    {
        var raw = RawRelative(info.FullName);
        var placeholder = CloudAttributes.IsPlaceholder(info);
        if (!placeholder && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            return new ScanItem.Skipped(raw, SkipReason.Link);
        }

        if (!RelativePath.TryParse(raw, out var path))
        {
            return new ScanItem.Skipped(raw, SkipReason.InvalidName);
        }

        try
        {
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            return info is FileInfo file
                ? new ScanItem.Found(new LocalEntry(path, NodeKind.File, file.Length, mtime, placeholder && CloudAttributes.IsCloudOnly(info.Attributes)))
                : new ScanItem.Found(new LocalEntry(path, NodeKind.Directory, 0, mtime));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return new ScanItem.Skipped(raw, SkipReason.Unreadable);
        }
    }

    private bool IsInternal(string fullPath)
    {
        var internalRoot = Path.Combine(_root, InternalFolder);
        return fullPath.Equals(internalRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(internalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string RawRelative(string fullPath) =>
        Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private string ToFullPath(RelativePath path) =>
        path.IsRoot ? _root : Path.Combine(_root, path.Value.Replace('/', Path.DirectorySeparatorChar));

    private sealed class StagedFile(FileStream stream, string tempPath, string targetPath) : IStagedFile
    {
        // Ein geöffnetes Ziel (Editor, Virenscanner) lässt sich unter Windows nicht ersetzen: kurz wiederholen, dann
        // sauber scheitern. Die Staging-Datei bleibt dabei erhalten, der Aufrufer kann den Commit später wiederholen.
        private const int MaxAttempts = 4;
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(50);

        private bool _closed;
        private bool _committed;

        public Stream Content => stream;

        public async Task CommitAsync(DateTimeOffset mtime, CancellationToken ct)
        {
            if (!_closed)
            {
                await stream.FlushAsync(ct);
                await stream.DisposeAsync();
                File.SetLastWriteTimeUtc(tempPath, mtime.UtcDateTime);
                _closed = true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                    _committed = true;
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < MaxAttempts)
                {
                    await Task.Delay(FirstDelay * (1 << (attempt - 1)), ct);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            if (!_committed)
            {
                File.Delete(tempPath);
            }
        }
    }
}
