using SkiaSharp;
using SyncDrive.Server.App.Previews;

namespace SyncDrive.Infra.Imaging;

/// <summary>
/// Vorschaubilder mit SkiaSharp (JPEG, PNG, GIF, WebP, BMP). Dekodiert gleich verkleinert, wo das Format es kann (JPEG), dreht nach der
/// EXIF-Ausrichtung und speichert als JPEG; Transparenz bekommt einen weißen Hintergrund.
/// </summary>
public sealed class SkiaThumbnailRenderer : IThumbnailRenderer
{
    private const int Quality = 82;

    public byte[]? Render(Stream source, int maxEdge)
    {
        using var codec = SKCodec.Create(source);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        var scale = Math.Min(1f, (float)maxEdge / Math.Max(info.Width, info.Height));
        var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var supported = codec.GetScaledDimensions(scale);
        var target = Math.Min(maxEdge, Math.Max(info.Width, info.Height));
        if (supported.Width < info.Width && Math.Max(supported.Width, supported.Height) >= target)
        {
            // JPEG kann beim Dekodieren in Stufen (½, ¼, ⅛) verkleinern: spart Speicher, aber nie unter die Zielgröße
            decodeInfo = decodeInfo.WithSize(supported.Width, supported.Height);
        }

        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        var fit = Math.Min(1f, (float)maxEdge / Math.Max(decoded.Width, decoded.Height));
        var (width, height) = (Math.Max(1, (int)Math.Round(decoded.Width * fit)), Math.Max(1, (int)Math.Round(decoded.Height * fit)));
        using var resized = fit < 1f ? decoded.Resize(new SKImageInfo(width, height), SKFilterQuality.High) : decoded.Copy();
        if (resized is null)
        {
            return null;
        }

        using var oriented = Orient(resized, codec.EncodedOrigin);
        using var surface = SKSurface.Create(new SKImageInfo(oriented.Width, oriented.Height));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawBitmap(oriented, 0, 0);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
        return data?.ToArray();
    }

    /// <summary>Dreht und spiegelt nach der EXIF-Ausrichtung, damit Fotos vom Telefon nicht quer liegen.</summary>
    private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        using var canvas = new SKCanvas(result);
        var (w, h) = (bitmap.Width, bitmap.Height);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1, w / 2f, 0); break;
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180, w / 2f, h / 2f); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1, 0, h / 2f); break;
            case SKEncodedOrigin.LeftTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1, 0, h / 2f); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, 0); canvas.RotateDegrees(90); canvas.Scale(-1, -1, w / 2f, h / 2f); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
        }

        canvas.DrawBitmap(bitmap, 0, 0);
        return result;
    }
}
