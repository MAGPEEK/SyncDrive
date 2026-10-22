using SyncDrive.Contracts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Previews;

/// <summary>Größe eines Vorschaubilds: klein für Listen, groß für die Vorschau.</summary>
public enum ThumbnailSize
{
    Small,
    Large,
}

/// <summary>
/// Vorschaubilder für Bilddateien (M7, 0.17.0). Erzeugt beim ersten Abruf, danach aus dem Zwischenspeicher, abgelegt nach Inhalt (Hash) und
/// Größe: Umbenennen kostet nichts, eine neue Fassung bekommt ein neues Bild. Vorschau dürfen auch Previewer sehen (dafür ist die Rolle da).
/// Es rendern höchstens zwei Bilder gleichzeitig, und zu große Dateien bekommen keine Vorschau, damit der Server im Speicherlimit bleibt.
/// </summary>
public sealed class PreviewService(SpaceService spaces, IThumbnailRenderer renderer, IThumbnailCache cache) : IDisposable
{
    public const long MaxSourceBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    private readonly SemaphoreSlim _rendering = new(2, 2);

    /// <summary>Lohnt sich ein Abruf? (Die Oberfläche fragt nur für solche Dateien.)</summary>
    public static bool HasPreview(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static int EdgeOf(ThumbnailSize size) => size == ThumbnailSize.Large ? 1024 : 128;

    public async Task<ServiceResult<Stream>> GetThumbnailAsync(SpaceView view, string path, ThumbnailSize size, CancellationToken ct)
    {
        var found = await view.GetNodeAsync(path, ct);
        if (!found.IsSuccess)
        {
            return ServiceResult.Fail<Stream>(found.Error!);
        }

        var node = found.Value!;
        if (node.Node.Deleted || node.Node.Kind != NodeKinds.File || !HasPreview(node.Node.Path) || node.Node.Hash is not { } hash
            || node.Node.Size is 0 or > MaxSourceBytes)
        {
            return NoPreview();
        }

        var key = hash.Replace("sha256:", string.Empty, StringComparison.Ordinal) + "-" + EdgeOf(size).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (await cache.OpenAsync(key, ct) is { } cached)
        {
            return ServiceResult.Ok(cached);
        }

        await _rendering.WaitAsync(ct);
        try
        {
            if (await cache.OpenAsync(key, ct) is { } meanwhile)
            {
                return ServiceResult.Ok(meanwhile); // ein anderer Abruf hat es gerade erzeugt
            }

            using var source = new MemoryStream((int)node.Node.Size);
            foreach (var block in node.Blocks)
            {
                var content = await spaces.OpenStoredBlockAsync(block, ct);
                if (!content.IsSuccess)
                {
                    return NoPreview();
                }

                await using var stream = content.Value!;
                await stream.CopyToAsync(source, ct);
            }

            source.Position = 0;
            if (renderer.Render(source, EdgeOf(size)) is not { } image)
            {
                return NoPreview();
            }

            await cache.SaveAsync(key, image, ct);
            return ServiceResult.Ok<Stream>(new MemoryStream(image, writable: false));
        }
        finally
        {
            _rendering.Release();
        }
    }

    public void Dispose() => _rendering.Dispose();

    private static ServiceResult<Stream> NoPreview() =>
        ServiceResult.Fail<Stream>(new ServiceError(ErrorCode.NotFound, "Für diese Datei gibt es keine Vorschau."));
}
