using SyncDrive.Server.App.Previews;

namespace SyncDrive.Infra.Blob;

/// <summary>Vorschaubilder als Dateien unter <c>&lt;root&gt;/ab/&lt;key&gt;.jpg</c>; geschrieben über eine Temp-Datei, nie halb sichtbar.</summary>
public sealed class FileThumbnailCache(string root) : IThumbnailCache
{
    private readonly string _root = Path.GetFullPath(root);

    public Task<Stream?> OpenAsync(string key, CancellationToken ct)
    {
        var path = PathOf(key);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous)
            : null);
    }

    public async Task SaveAsync(string key, byte[] image, CancellationToken ct)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, image, ct);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Der Schlüssel kommt aus Hash und Größe (nur Hex, Ziffern, Bindestrich); alles andere wird abgewiesen.</summary>
    private string PathOf(string key)
    {
        if (key.Length < 3 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Ungültiger Schlüssel.", nameof(key));
        }

        return Path.Combine(_root, key[..2], key + ".jpg");
    }
}
