using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Portal;

/// <summary>Gespeicherte Einstellungen der Anmeldeseite.</summary>
/// <param name="BackgroundType">MIME-Typ des Hintergrundbilds; null ohne eigenes Bild.</param>
/// <param name="BackgroundVersion">Wechselt mit jedem neuen Bild, damit Browser kein altes aus dem Cache zeigen.</param>
public sealed record PortalSettings(string ServerName, string? LoginMessage, string? BackgroundType, long BackgroundVersion);

/// <summary>Ablage der Anmeldeseite (Einstellungen und ein Hintergrundbild).</summary>
public interface IPortalStore
{
    Task<PortalSettings?> GetAsync(CancellationToken ct);

    Task SaveAsync(PortalSettings settings, CancellationToken ct);

    Task SaveBackgroundAsync(byte[] image, CancellationToken ct);

    Task<Stream?> OpenBackgroundAsync(CancellationToken ct);

    Task DeleteBackgroundAsync(CancellationToken ct);
}

/// <summary>
/// Anmeldeseite: links der selbst gewählte Servername mit einer Zeile Text, dahinter ein eigenes Hintergrundbild. Jeder darf
/// sie lesen (die Seite braucht sie vor der Anmeldung), ändern dürfen nur Administratoren. Bilder werden am Inhalt erkannt, nicht an der Endung.
/// </summary>
public sealed class PortalService(IPortalStore store, TimeProvider clock)
{
    public const string DefaultServerName = "SyncDrive";
    public const int MaxBackgroundBytes = 5 * 1024 * 1024;
    public const int MaxServerNameLength = 64;
    public const int MaxMessageLength = 200;

    public async Task<PortalSettings> GetAsync(CancellationToken ct) =>
        await store.GetAsync(ct) ?? new PortalSettings(DefaultServerName, null, null, 0);

    public async Task<ServiceResult<PortalSettings>> UpdateAsync(
        Principal caller, string? serverName, string? loginMessage, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Fail(ErrorCode.Forbidden, "Nur Administratoren ändern die Anmeldeseite.");
        }

        var name = serverName?.Trim() ?? string.Empty;
        var message = string.IsNullOrWhiteSpace(loginMessage) ? null : loginMessage.Trim();
        if (name.Length is 0 or > MaxServerNameLength || name.Any(char.IsControl))
        {
            return Fail(ErrorCode.InvalidRequest, $"Der Servername braucht 1 bis {MaxServerNameLength} Zeichen.");
        }

        if (message is { Length: > MaxMessageLength } || (message?.Any(char.IsControl) ?? false))
        {
            return Fail(ErrorCode.InvalidRequest, $"Der Text darf höchstens {MaxMessageLength} Zeichen haben.");
        }

        var updated = await GetAsync(ct) with { ServerName = name, LoginMessage = message };
        await store.SaveAsync(updated, ct);
        return ServiceResult.Ok(updated);
    }

    public async Task<ServiceResult<PortalSettings>> SetBackgroundAsync(Principal caller, byte[] image, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Fail(ErrorCode.Forbidden, "Nur Administratoren ändern die Anmeldeseite.");
        }

        if (image.Length is 0 or > MaxBackgroundBytes)
        {
            return Fail(ErrorCode.InvalidRequest, $"Das Bild darf höchstens {MaxBackgroundBytes / 1024 / 1024} MB groß sein.");
        }

        if (ImageType(image) is not { } type)
        {
            return Fail(ErrorCode.InvalidRequest, "Nur JPEG-, PNG- oder WebP-Bilder.");
        }

        await store.SaveBackgroundAsync(image, ct);
        var updated = await GetAsync(ct) with { BackgroundType = type, BackgroundVersion = clock.GetUtcNow().ToUnixTimeMilliseconds() };
        await store.SaveAsync(updated, ct);
        return ServiceResult.Ok(updated);
    }

    public async Task<ServiceResult<PortalSettings>> RemoveBackgroundAsync(Principal caller, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Fail(ErrorCode.Forbidden, "Nur Administratoren ändern die Anmeldeseite.");
        }

        await store.DeleteBackgroundAsync(ct);
        var updated = await GetAsync(ct) with { BackgroundType = null, BackgroundVersion = 0 };
        await store.SaveAsync(updated, ct);
        return ServiceResult.Ok(updated);
    }

    /// <summary>Das Bild samt Typ; null ohne eigenes Bild.</summary>
    public async Task<(Stream Content, string Type)?> OpenBackgroundAsync(CancellationToken ct) =>
        (await GetAsync(ct)).BackgroundType is { } type && await store.OpenBackgroundAsync(ct) is { } content ? (content, type) : null;

    /// <summary>Typ nach den ersten Bytes (JPEG, PNG, WebP); alles andere wird abgelehnt.</summary>
    public static string? ImageType(ReadOnlySpan<byte> data) =>
        data is [0xFF, 0xD8, 0xFF, ..] ? "image/jpeg"
        : data is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] ? "image/png"
        : data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    private static ServiceResult<PortalSettings> Fail(string code, string message) =>
        ServiceResult.Fail<PortalSettings>(new ServiceError(code, message));
}
