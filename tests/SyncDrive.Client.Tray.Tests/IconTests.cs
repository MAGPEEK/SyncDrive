using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using SyncDrive.Client.Tray.Controls;

namespace SyncDrive.Client.Tray.Tests;

/// <summary>Programmsymbol aus dem Grundzeichen; mit <c>SYNCDRIVE_WRITE_ICON=&lt;Datei&gt;</c> wird die ICO-Datei (neu) geschrieben.</summary>
public sealed class IconTests
{
    private static readonly int[] Sizes = [16, 32, 48, 256];

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
