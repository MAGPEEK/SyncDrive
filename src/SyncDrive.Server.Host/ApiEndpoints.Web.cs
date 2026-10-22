using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Portal;
using SyncDrive.Server.App.Previews;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.Host;

/// <summary>Was nur der Browser braucht (ADR-11): Anmeldung mit Sitzungs-Cookie und Herunterladen als Datenstrom.</summary>
public static partial class ApiEndpoints
{
    public const string SessionCookie = "syncdrive_session";
    public const string CsrfHeader = "X-SyncDrive-Web";
    private const string WebClientType = "web";

    private static void MapWeb(RouteGroupBuilder api, RouteGroupBuilder secured)
    {
        api.MapPost("/web/session", SignInWeb);
        secured.MapGet("/web/session", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
            Results.Ok(new WebSessionInfo(await accounts.UserNameOfAsync(Caller(http).UserId, ct) ?? "?", Caller(http).IsAdmin)));
        secured.MapDelete("/web/session", SignOutWeb);
        secured.MapGet("/spaces/{spaceId}/download", Download);
        secured.MapGet("/spaces/{spaceId}/zip", Zip);
        secured.MapGet("/spaces/{spaceId}/thumbnail", (string spaceId, string? path, string? size, HttpContext http, PreviewService previews, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Previewer, async view =>
            {
                var thumbnail = await previews.GetThumbnailAsync(
                    view, path ?? string.Empty, string.Equals(size, "large", StringComparison.OrdinalIgnoreCase) ? ThumbnailSize.Large : ThumbnailSize.Small, ct);
                if (!thumbnail.IsSuccess)
                {
                    return Problem(thumbnail.Error!);
                }

                http.Response.Headers.CacheControl = "private, max-age=3600"; // die Oberfläche hängt die Revision an, eine neue Fassung hat eine neue Adresse
                return Results.Stream(thumbnail.Value!, "image/jpeg");
            }, ct));
        MapPortal(api, secured);
        secured.MapGet("/spaces/{spaceId}/search", (string spaceId, string? q, string? path, int? limit, HttpContext http, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Previewer, async view =>
                Found(await view.SearchAsync(path ?? string.Empty, q ?? string.Empty, limit ?? SpaceService.MaxFindResults, ct)), ct));
        secured.MapGet("/spaces/{spaceId}/recent", (string spaceId, int? limit, HttpContext http, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Previewer, async view => Found(await view.RecentAsync(limit ?? 50, ct)), ct));
    }

    /// <summary>Anmeldeseite: lesen ohne Anmeldung, ändern nur als Administrator (Servername, Text, Hintergrundbild).</summary>
    private static void MapPortal(RouteGroupBuilder api, RouteGroupBuilder secured)
    {
        api.MapGet("/web/portal", async (PortalService portal, CancellationToken ct) => Results.Ok(ToInfo(await portal.GetAsync(ct))));
        api.MapGet("/web/portal/background", async (HttpContext http, PortalService portal, CancellationToken ct) =>
        {
            if (await portal.OpenBackgroundAsync(ct) is not { } background)
            {
                return Problem(new ServiceError(ErrorCode.NotFound, "Kein eigenes Hintergrundbild."));
            }

            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable"; // die Adresse wechselt mit jedem neuen Bild
            return Results.Stream(background.Content, background.Type);
        });
        secured.MapPut("/web/portal", async (PortalUpdateRequest request, HttpContext http, PortalService portal, CancellationToken ct) =>
            Portal(await portal.UpdateAsync(Caller(http), request.ServerName, request.LoginMessage, ct)));
        secured.MapPut("/web/portal/background", async (HttpContext http, PortalService portal, CancellationToken ct) =>
        {
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > PortalService.MaxBackgroundBytes)
                {
                    return Problem(new ServiceError(ErrorCode.InvalidRequest, "Bild zu groß."));
                }

                body.Write(buffer, 0, read);
            }

            return Portal(await portal.SetBackgroundAsync(Caller(http), body.ToArray(), ct));
        });
        secured.MapDelete("/web/portal/background", async (HttpContext http, PortalService portal, CancellationToken ct) =>
            Portal(await portal.RemoveBackgroundAsync(Caller(http), ct)));
    }

    private static IResult Portal(ServiceResult<PortalSettings> result) =>
        result.IsSuccess ? Results.Ok(ToInfo(result.Value!)) : Problem(result.Error!);

    private static PortalInfo ToInfo(PortalSettings settings) => new(
        settings.ServerName, settings.LoginMessage,
        settings.BackgroundType is null ? null : $"api/v1/web/portal/background?v={settings.BackgroundVersion}");

    private static IResult Found(ServiceResult<IReadOnlyList<SnapshotEntry>> result) =>
        result.IsSuccess ? Results.Ok(new FindResponse(result.Value!)) : Problem(result.Error!);

    /// <summary>Anmeldung im Browser: registriert ein Gerät vom Typ <c>web</c> und legt dessen Token in ein Cookie, das kein Skript lesen kann.</summary>
    private static async Task<IResult> SignInWeb(WebSignInRequest request, HttpContext http, AccountService accounts, CancellationToken ct)
    {
        if (!http.Request.Headers.ContainsKey(CsrfHeader))
        {
            return Problem(new ServiceError(ErrorCode.Forbidden, $"Kopfzeile {CsrfHeader} fehlt.")); // auch Anmelden nur von der eigenen Seite
        }

        var (registered, retryAfter) = await ThrottledRegisterAsync(http, accounts,
            new DeviceRegistrationRequest(request.UserName, request.Password, BrowserName(http.Request.Headers.UserAgent.ToString()), WebClientType, ProductVersion.Current),
            ct);
        if (retryAfter is { } wait)
        {
            return TooManyAttempts(http, wait);
        }

        if (!registered.IsSuccess)
        {
            return Problem(registered.Error!);
        }

        var principal = (await accounts.AuthenticateAsync(registered.Value!.Token, ct))!;
        http.Response.Cookies.Append(SessionCookie, registered.Value.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
        return Results.Ok(new WebSessionInfo(request.UserName.Trim(), principal.IsAdmin));
    }

    /// <summary>Abmelden widerruft das Browser-Gerät; das Cookie ist danach auch als Kopie wertlos.</summary>
    private static async Task<IResult> SignOutWeb(HttpContext http, AccountService accounts, CancellationToken ct)
    {
        await accounts.RevokeDeviceAsync(Caller(http), Caller(http).DeviceId, ct);
        http.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/", Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict });
        return Results.NoContent();
    }

    /// <summary>
    /// Eine Datei als Ganzes, Block für Block aus dem Speicher gestreamt (der Browser setzt nichts im Arbeitsspeicher zusammen). Mit
    /// <paramref name="revision"/> eine frühere Version („Versionen → Herunterladen“).
    /// </summary>
    private static Task<IResult> Download(
        string spaceId, string? path, long? revision, HttpContext http, SpaceService spaces, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Viewer, async view =>
        {
            var found = await view.GetNodeAsync(path ?? string.Empty, ct);
            if (!found.IsSuccess)
            {
                return Problem(found.Error!);
            }

            var node = found.Value!;
            if (node.Node.Deleted || node.Node.Kind != NodeKinds.File)
            {
                return Problem(new ServiceError(ErrorCode.NotFound, "Keine Datei."));
            }

            var (size, blocks) = (node.Node.Size, node.Blocks);
            if (revision is { } wanted && wanted != node.Node.Revision)
            {
                var versions = await view.ListVersionsAsync(node.Node.Path, ct);
                if (versions.Value?.Versions.FirstOrDefault(v => v.Revision == wanted) is not { Blocks: { } versionBlocks } version)
                {
                    return Problem(new ServiceError(ErrorCode.NotFound, "Version existiert nicht (mehr)."));
                }

                (size, blocks) = (version.Size, versionBlocks);
            }

            return FileResult(http, spaces, node.Node.Path, size, blocks, ct);
        }, ct);

    /// <summary>
    /// Ordner oder mehrere Elemente eines Ordners als ZIP (<c>?path=a&amp;path=b</c>), beim Senden gepackt. Ein Element heißt wie es selbst,
    /// mehrere wie ihr Ordner.
    /// </summary>
    private static Task<IResult> Zip(string spaceId, string[]? path, HttpContext http, SpaceService spaces, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Viewer, async view =>
            ZipResult(spaces, await view.PlanArchiveAsync(path ?? [], ct), ZipName(path ?? [], "SyncDrive"), ct), ct);

    /// <summary>Eine Datei aus ihren Blöcken als Anhang.</summary>
    internal static IResult FileResult(HttpContext http, SpaceService spaces, string path, long size, IReadOnlyList<string> blocks, CancellationToken ct)
    {
        http.Response.ContentLength = size;
        return Results.Stream(
            async body =>
            {
                foreach (var hash in blocks)
                {
                    var block = await spaces.OpenStoredBlockAsync(hash, ct);
                    await using var content = block.Value ?? throw new IOException($"Block {hash} fehlt.");
                    await content.CopyToAsync(body, ct);
                }
            },
            "application/octet-stream",
            path[(path.LastIndexOf('/') + 1)..]);
    }

    internal static IResult ZipResult(SpaceService spaces, ServiceResult<IReadOnlyList<ArchiveEntry>> plan, string fileName, CancellationToken ct) =>
        plan.IsSuccess
            ? Results.Stream(body => ArchiveWriter.WriteAsync(body, plan.Value!, spaces, ct), "application/zip", fileName)
            : Problem(plan.Error!);

    /// <summary>„Projekte.zip“: nach dem einen Element, sonst nach dem gemeinsamen Ordner; in der Wurzel nach <paramref name="rootName"/>.</summary>
    internal static string ZipName(IReadOnlyList<string> paths, string rootName)
    {
        var named = paths.Count == 1 ? paths[0] : paths.Count > 1 && paths[0].Contains('/') ? paths[0][..paths[0].LastIndexOf('/')] : rootName;
        return named[(named.LastIndexOf('/') + 1)..] + ".zip";
    }

    private static bool IsSafeMethod(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    /// <summary>Gerätename in der Geräteliste: „Browser (Firefox)“ usw., nie der ganze User-Agent.</summary>
    private static string BrowserName(string userAgent) => "Browser (" + (
        userAgent.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
        : userAgent.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
        : userAgent.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome"
        : userAgent.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
        : "Web") + ")";
}
