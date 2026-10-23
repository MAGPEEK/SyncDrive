using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>
/// Kleiner Identitätsanbieter auf 127.0.0.1 für die Tests: Discovery, Autorisierung (gibt sofort einen Code für den vorgemerkten Benutzer),
/// Token-Endpunkt mit Prüfung von Client-Secret, Redirect-URI und PKCE, UserInfo.
/// </summary>
public sealed class FakeIdentityProvider : IAsyncDisposable
{
    public const string ClientId = "syncdrive";
    public const string ClientSecret = "test-secret";
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, (string Challenge, string RedirectUri, string Sub, string Name)> _codes = new();
    private readonly ConcurrentDictionary<string, (string Sub, string Name)> _tokens = new();

    private FakeIdentityProvider(WebApplication app) => _app = app;

    public string Issuer { get; private set; } = string.Empty;

    /// <summary>Wer sich beim nächsten <c>authorize</c> „anmeldet“.</summary>
    public (string Sub, string Name) NextUser { get; set; } = ("sub-1", "nobody");

    public static async Task<FakeIdentityProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var idp = new FakeIdentityProvider(app);
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, string>
        {
            ["issuer"] = idp.Issuer,
            ["authorization_endpoint"] = idp.Issuer + "/authorize",
            ["token_endpoint"] = idp.Issuer + "/token",
            ["userinfo_endpoint"] = idp.Issuer + "/userinfo",
        }));
        app.MapGet("/authorize", (HttpRequest request) =>
        {
            var q = request.Query;
            if (q["client_id"] != ClientId || q["response_type"] != "code" || q["code_challenge_method"] != "S256")
            {
                return Results.BadRequest();
            }

            var code = Guid.NewGuid().ToString("N");
            idp._codes[code] = (q["code_challenge"]!, q["redirect_uri"]!, idp.NextUser.Sub, idp.NextUser.Name);
            return Results.Redirect($"{q["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(q["state"]!)}");
        });
        app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            if (form["client_secret"] != ClientSecret || !idp._codes.TryRemove(form["code"].ToString(), out var grant)
                || grant.RedirectUri != form["redirect_uri"]
                || Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_') != grant.Challenge)
            {
                return Results.BadRequest(new { error = "invalid_grant" });
            }

            var token = Guid.NewGuid().ToString("N");
            idp._tokens[token] = (grant.Sub, grant.Name);
            return Results.Json(new { access_token = token, token_type = "Bearer" });
        });
        app.MapGet("/userinfo", (HttpRequest request) =>
            idp._tokens.TryGetValue(request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.Ordinal), out var user)
                ? Results.Json(new Dictionary<string, object> { ["sub"] = user.Sub, ["preferred_username"] = user.Name, ["email_verified"] = true })
                : Results.Unauthorized());
        await app.StartAsync();
        idp.Issuer = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return idp;
    }

    /// <summary>Was der Browser beim Anbieter tut: der Adresse folgen und die Rücksprungadresse mitnehmen.</summary>
    public static async Task<Uri> AuthorizeAsync(Uri authorizeUrl)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await http.GetAsync(authorizeUrl);
        return response.Headers.Location ?? throw new InvalidOperationException("Anbieter leitete nicht zurück: " + response.StatusCode);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>Organisationseinstellungen → Anmeldung über OAuth 2.0 (0.35.0): Ablauf, Verknüpfung, Ablehnungen, Rechte.</summary>
public class OAuthSignInTests(HostFactory factory) : IClassFixture<HostFactory>, IAsyncLifetime
{
    private FakeIdentityProvider _idp = null!;

    public async Task InitializeAsync() => _idp = await FakeIdentityProvider.StartAsync();

    public async Task DisposeAsync() => await _idp.DisposeAsync();

    private HttpClient Browser() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private async Task ConfigureAsync(bool createUsers)
    {
        var admin = await factory.LoginAsync();
        using var saved = await admin.Http.PutAsJsonAsync("/api/v1/admin/oauth", new OAuthSettingsRequest(
            true, "Test-IdP", _idp.Issuer, null, null, null, FakeIdentityProvider.ClientId, FakeIdentityProvider.ClientSecret, null, null, createUsers));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
    }

    /// <summary>Ein ganzer Durchlauf wie im Browser; liefert die Zieladresse nach dem Callback.</summary>
    private async Task<(Uri Target, HttpClient Browser)> SignInAsync(string sub, string name, Func<Uri, Uri>? tamper = null)
    {
        _idp.NextUser = (sub, name);
        var browser = Browser();
        using var start = await browser.GetAsync("/api/v1/web/oauth/start");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var back = await FakeIdentityProvider.AuthorizeAsync(start.Headers.Location!);
        using var callback = await browser.GetAsync((tamper ?? (u => u))(back).PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return (callback.Headers.Location!, browser);
    }

    private static async Task<WebSessionInfo?> SessionAsync(HttpClient browser)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/web/session");
        request.Headers.Add("X-SyncDrive-Web", "1");
        using var response = await browser.SendAsync(request);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<WebSessionInfo>() : null;
    }

    [Fact]
    public async Task A_new_user_signs_in_through_the_provider_and_gets_an_account_and_a_session()
    {
        await ConfigureAsync(createUsers: true);
        var name = "carla-" + Guid.NewGuid().ToString("N")[..8];

        var login = await Browser().GetFromJsonAsync<OAuthLoginInfo>("/api/v1/web/oauth");
        var (target, browser) = await SignInAsync("sub-" + name, name);
        var session = await SessionAsync(browser);
        var (again, browser2) = await SignInAsync("sub-" + name, name); // zweites Mal: über die Verknüpfung

        Assert.Equal(new OAuthLoginInfo(true, "Test-IdP"), login);
        Assert.Equal("/", target.OriginalString);
        Assert.Equal((name, false), (session!.UserName, session.IsAdmin));
        Assert.Equal("/", again.OriginalString);
        Assert.NotNull(await SessionAsync(browser2));
    }

    [Fact]
    public async Task An_existing_account_is_linked_once_and_cannot_be_taken_over_by_another_provider_account()
    {
        await ConfigureAsync(createUsers: false);
        var name = await factory.CreateUserAsync("dora-" + Guid.NewGuid().ToString("N")[..8]);

        var (first, _) = await SignInAsync("sub-dora", name);
        var (takeover, intruder) = await SignInAsync("sub-mallory", name); // gleicher Name, anderes Konto beim Anbieter
        var (unknown, _) = await SignInAsync("sub-x", "gibt-es-nicht-" + Guid.NewGuid().ToString("N")[..6]);

        Assert.Equal("/", first.OriginalString);
        Assert.Equal("/?signin_error=" + OAuthRefusal.LinkedElsewhere, takeover.OriginalString);
        Assert.Null(await SessionAsync(intruder));
        Assert.Equal("/?signin_error=" + OAuthRefusal.NoAccount, unknown.OriginalString);
    }

    [Fact]
    public async Task A_forged_state_or_code_is_refused()
    {
        await ConfigureAsync(createUsers: true);

        var (forgedState, browser) = await SignInAsync("sub-eve", "eve", u => new Uri(u.OriginalString.Replace("state=", "state=x", StringComparison.Ordinal)));
        var (forgedCode, _) = await SignInAsync("sub-eve", "eve", u => new Uri(u.OriginalString.Replace("code=", "code=x", StringComparison.Ordinal)));
        using var noCookie = await Browser().GetAsync("/api/v1/web/oauth/callback?code=abc&state=def");

        Assert.Equal("/?signin_error=" + OAuthRefusal.Expired, forgedState.OriginalString);
        Assert.Null(await SessionAsync(browser));
        Assert.Equal("/?signin_error=" + OAuthRefusal.Provider, forgedCode.OriginalString);
        Assert.Equal("/?signin_error=" + OAuthRefusal.Expired, noCookie.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Only_admins_see_the_settings_and_never_the_client_secret()
    {
        await ConfigureAsync(createUsers: false);
        var admin = await factory.LoginAsync();
        var user = await factory.LoginAsync(await factory.CreateUserAsync("eric-" + Guid.NewGuid().ToString("N")[..8]), "another-long-password");

        var raw = await admin.Http.GetStringAsync("/api/v1/admin/oauth");
        var settings = (await admin.Http.GetFromJsonAsync<OAuthSettingsInfo>("/api/v1/admin/oauth"))!;
        using var asUser = await user.Http.GetAsync("/api/v1/admin/oauth");
        using var insecure = await admin.Http.PutAsJsonAsync("/api/v1/admin/oauth", new OAuthSettingsRequest(
            true, "X", null, "http://idp.example.org/authorize", "https://idp.example.org/token", "https://idp.example.org/userinfo", "c", null, null, null, false));

        Assert.DoesNotContain(FakeIdentityProvider.ClientSecret, raw, StringComparison.Ordinal);
        Assert.True(settings.HasClientSecret);
        Assert.Equal(_idp.Issuer + "/token", settings.TokenEndpoint); // aus der Discovery
        Assert.EndsWith("/api/v1/web/oauth/callback", settings.RedirectUri, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, asUser.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, insecure.StatusCode); // http nur für localhost
    }
}
