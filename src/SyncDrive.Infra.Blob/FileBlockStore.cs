using System.Security.Cryptography;
using SyncDrive.Core.Model;
using SyncDrive.Server.App.Ports;

namespace SyncDrive.Infra.Blob;

/// <summary>
/// Blockspeicher im Dateisystem unter <c>&lt;root&gt;/ab/cd/&lt;hex&gt;</c>. Geschrieben wird zuerst in eine Temp-Datei und
/// erst nach erfolgreicher Hash-Prüfung atomar an den Zielort verschoben; ein halber Block ist nie sichtbar.
/// </summary>
public sealed class FileBlockStore : IBlockStore
{
    private const string Prefix = "sha256:";
    private readonly string _root;
    private readonly string _tempDirectory;

    public FileBlockStore(string root)
    {
        _root = Path.GetFullPath(root);
        _tempDirectory = Path.Combine(_root, ".tmp");
        Directory.CreateDirectory(_tempDirectory);
    }

    public Task<long?> GetSizeAsync(ContentHash hash, CancellationToken ct)
    {
        var info = new FileInfo(PathOf(hash));
        return Task.FromResult(info.Exists ? (long?)info.Length : null);
    }

    public async Task<BlockWriteResult> WriteAsync(ContentHash expected, Stream content, CancellationToken ct)
    {
        var tempPath = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var temp = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await temp.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (ContentHash.FromBytes(hasher.GetHashAndReset()) != expected)
            {
                return BlockWriteResult.HashMismatch;
            }

            var target = PathOf(expected);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
            {
                try
                {
                    File.Move(tempPath, target);
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Ein gleichzeitiger Schreiber war schneller; derselbe Hash heißt derselbe Inhalt.
                }
            }

            return BlockWriteResult.Stored;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    public Task<Stream?> OpenReadAsync(ContentHash hash, CancellationToken ct)
    {
        var path = PathOf(hash);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous)
            : null);
    }

    /// <summary>
    /// Sicherung: kopiert alle Blöcke, die unter <paramref name="targetRoot"/> noch fehlen, in derselben Ordnung. Blöcke ändern sich nie
    /// (inhaltsadressiert), deshalb genügt „fehlt noch“; jede Kopie geht erst unter einem Temp-Namen, damit nie ein halber Block liegen bleibt.
    /// </summary>
    /// <returns>Anzahl neu kopierter Blöcke.</returns>
    public async Task<int> CopyMissingToAsync(string targetRoot, CancellationToken ct)
    {
        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_root, source);
            if (relative.StartsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.Combine(targetRoot, relative);
            if (File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".partial";
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            await using (var to = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await from.CopyToAsync(to, ct);
            }

            File.Move(temp, target, overwrite: true);
            copied++;
        }

        return copied;
    }

    private string PathOf(ContentHash hash)
    {
        var hex = hash.Value[Prefix.Length..]; // ContentHash garantiert 64 Hex-Zeichen, kein Pfadausbruch möglich
        return Path.Combine(_root, hex[..2], hex[2..4], hex);
    }
}
