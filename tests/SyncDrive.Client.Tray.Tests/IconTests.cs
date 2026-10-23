using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using SyncDrive.Client.Tray.Controls;

namespace SyncDrive.Client.Tray.Tests;

/// <summary>Programmsymbol aus dem Grundzeichen; mit <c>SYNCDRIVE_WRITE_ICON=&lt;Datei&gt;</c> wird die ICO-Datei (neu) geschrieben.</summary>
public sealed class IconTests
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256]; // Zwischengrößen für 125 % und 150 %: sonst skaliert Windows unscharf

    [AvaloniaFact]
    public void The_app_icon_contains_all_sizes_as_png()
    {
        var images = Sizes.Select(Render).ToList();
        var ico = Ico(images);

        Assert.Equal([0, 0, 1, 0, Sizes.Length, 0], ico.Take(6).Select(b => (int)b));
        if (Environment.GetEnvironmentVariable("SYNCDRIVE_WRITE_ICON") is { Length: > 0 } target)
        {
            File.WriteAllBytes(target, ico);
        }
    }

    /// <summary>
    /// Symbolsatz für das macOS-App-Paket (<c>iconutil -c icns</c> macht daraus <c>SyncDrive.icns</c>); mit
    /// <c>SYNCDRIVE_WRITE_ICONSET=&lt;Ordner&gt;.iconset</c> geschrieben, sonst nur gezeichnet und geprüft.
    /// </summary>
    [AvaloniaFact]
    public void The_mac_icon_set_has_every_size_iconutil_expects()
    {
        var target = Environment.GetEnvironmentVariable("SYNCDRIVE_WRITE_ICONSET");
        var names = new List<string>();
        foreach (var points in new[] { 16, 32, 128, 256, 512 })
        {
            foreach (var scale in new[] { 1, 2 })
            {
                var name = scale == 1 ? $"icon_{points}x{points}.png" : $"icon_{points}x{points}@2x.png";
                var (_, png) = RenderMac(points * scale);
                names.Add(name);
                if (target is { Length: > 0 })
                {
                    Directory.CreateDirectory(target);
                    File.WriteAllBytes(Path.Combine(target, name), png);
                }
            }
        }

        Assert.Equal(10, names.Count);
    }

    private static (int Size, byte[] Png) Render(int size)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            Badges.DrawGlyph(ctx, new Rect(0, 0, size, size));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return (size, stream.ToArray());
    }

    /// <summary>
    /// macOS-Programmsymbol nach Apples Raster: Die Kachel nimmt 824 von 1024 Punkten ein, mittig mit Rand; randlos wirkte sie im Dock zu groß
    /// und flach neben den anderen Symbolen.
    /// </summary>
    private static (int Size, byte[] Png) RenderMac(int size)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var body = size * 824.0 / 1024;
            var margin = (size - body) / 2;
            Badges.DrawGlyph(ctx, new Rect(margin, margin, body, body));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return (size, stream.ToArray());
    }

    /// <summary>ICO mit PNG-Bildern (seit Windows Vista erlaubt): Kopf, Verzeichnis, Bilddaten.</summary>
    private static byte[] Ico(List<(int Size, byte[] Png)> images)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        var offset = 6 + (16 * images.Count);
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images)
        {
            writer.Write(png);
        }

        return stream.ToArray();
    }
}
