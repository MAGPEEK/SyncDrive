using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Server.Host;

/// <summary>
/// Anmeldung über einen Identitätsanbieter (OAuth 2.0 Authorization Code mit PKCE). <c>start</c> merkt sich <c>state</c> und Verifier in
/// einem kurzlebigen Cookie (<c>SameSite=Lax</c>, damit es die Rückkehr vom Anbieter mitbringt) und leitet weiter; <c>callback</c> prüft,
/// meldet das Browser-Gerät an, setzt das Sitzungs-Cookie wie die Passwort-Anmeldung und kehrt zur Oberfläche zurück.
/// </summary>
public static partial class ApiEndpoints
{
    private const string OAuthCookie = "syncdrive_oauth";
    private const string OAuthPath = "/api/v1/web/oauth";

    private static void MapOAuth(RouteGroupBuilder api, RouteGroupBuilder secured)
    {
        api.MapGet("/web/oauth", async (ExternalSignInService oauth, CancellationToken ct) => Results.Ok(await oauth.LoginInfoAsync(ct)));
        api.MapGet("/web/oauth/start", StartOAuth);
        api.MapGet("/web/oauth/callback", OAuthCallback);
        secured.MapGet("/admin/oauth", async (HttpContext http, ExternalSignInService oauth, CancellationToken ct) =>
            Respond(await oauth.GetAsync(Caller(http), RedirectUri(http), ct)));
        secured.MapPut("/admin/oauth", async (OAuthSettingsRequest request, HttpContext http, ExternalSignInService oauth, CancellationToken ct) =>
            Respond(await oauth.SaveAsync(Caller(http), request, RedirectUri(http), ct)));
    }

    private static async Task<IResult> StartOAuth(HttpContext http, ExternalSignInService oauth, CancellationToken ct)
    {
        if (await oauth.BeginAsync(RedirectUri(http), ct) is not { } start)
        {
            return Results.Redirect("/?signin_error=" + OAuthRefusal.Off);
        }

        http.Response.Cookies.Append(OAuthCookie, start.State + "." + start.Verifier, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = OAuthPath,
            MaxAge = TimeSpan.FromMinutes(10),
            IsEssential = true,
        });
        return Results.Redirect(start.AuthorizeUrl);
    }

    private static async Task<IResult> OAuthCallback(
        string? code, string? state, string? error, HttpContext http, ExternalSignInService oauth, CancellationToken ct)
    {
        var remembered = http.Request.Cookies[OAuthCookie]?.Split('.', 2);
        http.Response.Cookies.Delete(OAuthCookie, new CookieOptions { Path = OAuthPath, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax });
        var result = await oauth.CompleteAsync(
            code, state, error, remembered?.ElementAtOrDefault(0), remembered?.ElementAtOrDefault(1), RedirectUri(http),
            BrowserName(http.Request.Headers.UserAgent.ToString()), ct);
        if (!result.IsSuccess)
        {
            return Results.Redirect("/?signin_error=" + Uri.EscapeDataString(result.Error!.Message));
        }

        http.Response.Cookies.Append(SessionCookie, result.Value!.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
        return Results.Redirect("/");
    }

    /// <summary>Rücksprungadresse, die beim Anbieter eingetragen wird; hinter einem Proxy mit <c>TrustForwardedHeaders</c> dessen Adresse.</summary>
    private static string RedirectUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{OAuthPath}/callback";
}
