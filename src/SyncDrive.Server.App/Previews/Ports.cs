namespace SyncDrive.Server.App.Previews;

/// <summary>Verkleinert ein Bild; null, wenn es sich nicht lesen lässt (beschädigt, unbekanntes Format).</summary>
public interface IThumbnailRenderer
{
    /// <summary>JPEG mit der längeren Kante höchstens <paramref name="maxEdge"/> Pixel, Ausrichtung aus den Bilddaten (EXIF) berücksichtigt.</summary>
    byte[]? Render(Stream source, int maxEdge);
}

/// <summary>Fertige Vorschaubilder, nach Inhalt und Größe abgelegt (gleicher Inhalt in mehreren Dateien = ein Vorschaubild).</summary>
public interface IThumbnailCache
{
    Task<Stream?> OpenAsync(string key, CancellationToken ct);

    Task SaveAsync(string key, byte[] image, CancellationToken ct);
}
