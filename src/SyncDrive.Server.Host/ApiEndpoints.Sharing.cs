using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Items;
using SyncDrive.Server.App.Sharing;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.Host;

/// <summary>
/// Freigabe (M6b, ADR-12): Einladungen, öffentliche Links und Dateianfragen verwalten; öffentliche Links ohne Anmeldung auflösen. Ein
/// eingegebenes Link-Passwort merkt sich der Browser in einem Cookie, das nur für diesen Link gilt und bei einem neuen Passwort verfällt.
/// </summary>
public static partial class ApiEndpoints
{
    private const string LinkCookiePrefix = "syncdrive_link_";
    private const string LinkProtectorPurpose = "SyncDrive.PublicLink.v1";

    private static void MapSharing(RouteGroupBuilder api, RouteGroupBuilder secured)
    {
        secured.MapGet("/spaces/{spaceId}/sharing", async (string spaceId, string? path, HttpContext http, SharingService sharing, CancellationToken ct) =>
            Respond(await sharing.GetSharingAsync(Caller(http), spaceId, path ?? string.Empty, ct)));
        secured.MapPost("/spaces/{spaceId}/sharing/invitees", Invite);
        secured.MapDelete("/sharing/invitees/{shareId}", async (string shareId, HttpContext http, SharingService sharing, CancellationToken ct) =>
            await sharing.RemoveInviteAsync(Caller(http), shareId, ct) is { } error ? Problem(error) : Results.NoContent());
        secured.MapPut("/spaces/{spaceId}/sharing/link", async (
            string spaceId, string? path, string? kind, LinkRequest request, HttpContext http, SharingService sharing, CancellationToken ct) =>
            Respond(await sharing.SetLinkAsync(Caller(http), spaceId, path ?? string.Empty, kind ?? LinkKinds.Share, request, ct)));
        secured.MapDelete("/sharing/links/{linkId}", async (string linkId, HttpContext http, SharingService sharing, CancellationToken ct) =>
            await sharing.RemoveLinkAsync(Caller(http), linkId, ct) is { } error ? Problem(error) : Results.NoContent());
        secured.MapGet("/sharing/by-me", async (HttpContext http, SharingService sharing, CancellationToken ct) =>
            Results.Ok(await sharing.ListSharedByMeAsync(Caller(http), ct)));

        var link = api.MapGroup("/public/{token}");
        link.MapGet(string.Empty, (string token, HttpContext http, PublicLinkService links, CancellationToken ct) =>
            WithLink(http, token, links, requireUnlocked: false, (access, unlocked) =>
                Task.FromResult(Results.Ok(PublicLinkService.Describe(access, unlocked))), ct));
        link.MapPost("/password", UnlockLink);
        link.MapGet("/list", (string token, string? path, HttpContext http, PublicLinkService links, CancellationToken ct) =>
            WithLink(http, token, links, requireUnlocked: true, async (access, _) =>
                Found(await PublicLinkService.ListAsync(access, path, ct)), ct));
        link.MapGet("/download", (string token, string? path, HttpContext http, PublicLinkService links, SpaceService spaces, CancellationToken ct) =>
            WithLink(http, token, links, requireUnlocked: true, async (access, _) =>
            {
                var file = await links.OpenFileAsync(access, path, ct);
                return file.IsSuccess
                    ? FileResult(http, spaces, file.Value!.Node.Path, file.Value.Node.Size, file.Value.Blocks, ct)
                    : Problem(file.Error!);
            }, ct));
        link.MapGet("/zip", (string token, string[]? path, HttpContext http, PublicLinkService links, SpaceService spaces, CancellationToken ct) =>
            WithLink(http, token, links, requireUnlocked: true, async (access, _) =>
            {
                var folderName = access.Target.Path[(access.Target.Path.LastIndexOf('/') + 1)..];
                if (path is { Length: > 0 })
                {
                    return ZipResult(spaces, await PublicLinkService.PlanArchiveAsync(access, path, ct), ZipName(path, folderName), ct);
                }

                // ohne Auswahl der ganze Ordner: sein Inhalt, benannt nach ihm
                var listed = await PublicLinkService.ListAsync(access, string.Empty, ct);
                if (!listed.IsSuccess || listed.Value!.Count == 0)
                {
                    return Problem(listed.Error ?? new ServiceError(ErrorCode.NotFound, "Der Ordner ist leer."));
                }

                return ZipResult(
                    spaces, await PublicLinkService.PlanArchiveAsync(access, [.. listed.Value.Select(e => e.Path)], ct), folderName + ".zip", ct);
            }, ct));
        link.MapPut("/files", UploadToLink);
        link.MapPost("/blocks/missing", (string token, MissingBlocksRequest request, HttpContext http, PublicLinkService links, CancellationToken ct) =>
            WithLink(http, token, links, requireUnlocked: true, async (access, _) =>
                Respond(await links.MissingBlocksAsync(access, request.Hashes ?? [], ct)), ct));
        link.MapPut("/blocks/{hash}", (string token, string hash, HttpContext http, PublicLinkService links, IOptions<ServerOptions> options, CancellationToken ct) =>
            WithLink(http, token, links, requireUnlocked: true, async (access, _) =>
            {
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                {
                    limit.MaxRequestBodySize = options.Value.MaxBlockBytes;
                }

                var stored = await links.PutBlockAsync(access, hash, http.Request.Body, ct);
                return stored.IsSuccess ? Results.NoContent() : Problem(stored.Error!);
            }, ct));
        link.MapPost("/files", (string token, PublicFileRequest request, HttpContext http, PublicLinkService links, IOptions<ServerOptions> options, CancellationToken ct) =>
            WithLink(http, token, links, requireUnlocked: true, async (access, _) =>
                Respond(await links.CommitUploadAsync(access, request, options.Value.MaxPublicUploadBytes, ct)), ct));
    }

    private static async Task<IResult> Invite(
        string spaceId, string? path, InviteRequest request, HttpContext http, SharingService sharing, NotificationService notes, CancellationToken ct)
    {
        var invited = await sharing.InviteAsync(Caller(http), spaceId, path ?? string.Empty, request, ct);
        if (!invited.IsSuccess)
        {
            return Problem(invited.Error!);
        }

        await notes.SharedAsync(Caller(http), invited.Value!.ShareId, invited.Value.UserName, ct);
        return Results.Ok(invited.Value);
    }

    private static Task<IResult> UnlockLink(
        string token, LinkPasswordRequest request, HttpContext http, PublicLinkService links, IDataProtectionProvider protection, CancellationToken ct) =>
        WithLink(http, token, links, requireUnlocked: false, (access, unlocked) =>
        {
            var limiter = http.RequestServices.GetRequiredService<AttemptLimiter>();
            var keys = LinkKeys(http, access.Link.Id);
            if (!unlocked && limiter.RetryAfter(keys) is { } wait)
            {
                return Task.FromResult(TooManyAttempts(http, wait));
            }

            if (!unlocked && !links.VerifyPassword(access, request.Password))
            {
                limiter.Failed(keys);
                return Task.FromResult(Problem(new ServiceError(ErrorCode.PasswordRequired, "Falsches Passwort.")));
            }

            if (access.Link.PasswordHash is not null)
            {
                http.Response.Cookies.Append(LinkCookiePrefix + access.Link.Id, protection.CreateProtector(LinkProtectorPurpose).Protect(Grant(access)),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = http.Request.IsHttps,
                        SameSite = SameSiteMode.Strict,
                        Path = "/api/v1/public/" + token,
                        IsEssential = true,
                    });
            }

            return Task.FromResult(Results.Ok(PublicLinkService.Describe(access, unlocked: true)));
        }, ct);

    /// <summary>Dateianfrage: der Rumpf ist die Datei; Name aus <c>?name=</c>. Größe begrenzt durch <see cref="ServerOptions.MaxPublicUploadBytes"/>.</summary>
    private static Task<IResult> UploadToLink(
        string token, string? name, HttpContext http, PublicLinkService links, IOptions<ServerOptions> options, CancellationToken ct) =>
        WithLink(http, token, links, requireUnlocked: true, async (access, _) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = options.Value.MaxPublicUploadBytes;
            }

            return Respond(await links.UploadAsync(access, name, http.Request.Body, options.Value.MaxPublicUploadBytes, ct));
        }, ct);

    /// <summary>Löst den Link auf und prüft, ob sein Passwort (falls es eines gibt) schon eingegeben wurde.</summary>
    private static async Task<IResult> WithLink(
        HttpContext http, string token, PublicLinkService links, bool requireUnlocked, Func<PublicAccess, bool, Task<IResult>> action, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var access = await links.ResolveAsync(token, ct);
        if (!access.IsSuccess)
        {
            return Problem(access.Error!);
        }

        var unlocked = access.Value!.Link.PasswordHash is null || HasGrant(http, access.Value);
        if (requireUnlocked && !unlocked)
        {
            return Problem(new ServiceError(ErrorCode.PasswordRequired, "Passwort erforderlich."));
        }

        return await action(access.Value, unlocked);
    }

    private static bool HasGrant(HttpContext http, PublicAccess access)
    {
        if (!http.Request.Cookies.TryGetValue(LinkCookiePrefix + access.Link.Id, out var cookie))
        {
            return false;
        }

        try
        {
            var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(LinkProtectorPurpose);
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(protector.Unprotect(cookie)), Encoding.UTF8.GetBytes(Grant(access)));
        }
        catch (CryptographicException)
        {
            return false; // verfälscht oder nach einem Neustart mit neuem Schlüssel
        }
    }

    /// <summary>Was das Cookie bestätigt: dieser Link mit diesem Passwort. Ein neues Passwort macht alte Cookies wertlos.</summary>
    private static string Grant(PublicAccess access) =>
        access.Link.Id + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(access.Link.PasswordHash ?? string.Empty)))[..16];
}
