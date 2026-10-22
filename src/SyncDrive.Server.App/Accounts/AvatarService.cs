using SyncDrive.Contracts;
using SyncDrive.Server.App.Portal;
using SyncDrive.Server.App.Previews;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Accounts;

/// <summary>Profilbilder je Benutzer, schon verkleinert (JPEG).</summary>
public interface IAvatarStore
{
    Task SaveAsync(string userId, byte[] jpeg, CancellationToken ct);

    Task<Stream?> OpenAsync(string userId, CancellationToken ct);

    Task DeleteAsync(string userId, CancellationToken ct);
}

/// <summary>
/// Eigenes Profilbild: hochgeladen wird JPEG, PNG oder WebP bis <see cref="MaxBytes"/>; gespeichert wird eine Fassung mit höchstens
/// <see cref="Edge"/> Pixeln Kantenlänge (Ausrichtung aus den Bilddaten berücksichtigt), damit nie das Originalfoto ausgeliefert wird.
/// </summary>
public sealed class AvatarService(IAvatarStore store, IThumbnailRenderer renderer)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int Edge = 256;

    public async Task<ServiceError?> SetAsync(Principal caller, byte[] image, CancellationToken ct)
    {
        if (image.Length is 0 or > MaxBytes)
        {
            return new ServiceError(ErrorCode.InvalidRequest, $"Das Bild darf höchstens {MaxBytes / 1024 / 1024} MB groß sein.");
        }

        if (PortalService.ImageType(image) is null)
        {
            return new ServiceError(ErrorCode.InvalidRequest, "Nur JPEG-, PNG- oder WebP-Bilder.");
        }

        using var source = new MemoryStream(image, writable: false);
        if (renderer.Render(source, Edge) is not { } small)
        {
            return new ServiceError(ErrorCode.InvalidRequest, "Das Bild lässt sich nicht lesen.");
        }

        await store.SaveAsync(caller.UserId, small, ct);
        return null;
    }

    public Task RemoveAsync(Principal caller, CancellationToken ct) => store.DeleteAsync(caller.UserId, ct);

    /// <summary>Das Bild als JPEG; null, wenn der Benutzer keins hat.</summary>
    public Task<Stream?> OpenAsync(string userId, CancellationToken ct) => store.OpenAsync(userId, ct);
}
