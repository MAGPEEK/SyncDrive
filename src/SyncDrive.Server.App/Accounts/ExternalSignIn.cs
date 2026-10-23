using System.Security.Cryptography;
using System.Text;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Accounts;

/// <summary>Gespeicherte Anbindung an einen Identitätsanbieter (OAuth 2.0 mit PKCE, Benutzer über UserInfo).</summary>
public sealed record OAuthSettings(
    bool Enabled = false,
    string DisplayName = "",
    string Issuer = "",
    string AuthorizationEndpoint = "",
    string TokenEndpoint = "",
    string UserInfoEndpoint = "",
    string ClientId = "",
    string? ClientSecret = null,
    string Scopes = "openid profile email",
    string UserNameClaim = "preferred_username",
    bool CreateUsers = false);

/// <summary>Anbindung und Verknüpfung externer Konten (Anbieter-Kennung + <c>sub</c> → Benutzer).</summary>
public interface IOAuthStore
{
    Task<OAuthSettings?> LoadAsync(CancellationToken ct);

    Task SaveAsync(OAuthSettings settings, CancellationToken ct);

    Task<string?> FindLinkedUserAsync(string provider, string subject, CancellationToken ct);

    /// <summary>Das <c>sub</c>, mit dem der Benutzer bei diesem Anbieter verknüpft ist; null ohne Verknüpfung.</summary>
    Task<string?> FindSubjectAsync(string provider, string userId, CancellationToken ct);

    Task LinkAsync(string provider, string subject, string userId, CancellationToken ct);
}

/// <summary>Was vom Anbieter kommt: Endpunkte aus der Discovery, Zugriffstoken, Angaben zum Benutzer.</summary>
public interface IOAuthProvider
{
    /// <summary><c>{issuer}/.well-known/openid-configuration</c>; null, wenn nicht lesbar.</summary>
    Task<(string Authorization, string Token, string UserInfo)?> DiscoverAsync(string issuer, CancellationToken ct);

    /// <summary>Tauscht den Code gegen ein Zugriffstoken (mit PKCE-Verifier); null bei Ablehnung.</summary>
    Task<string?> ExchangeCodeAsync(OAuthSettings settings, string code, string redirectUri, string verifier, CancellationToken ct);

    /// <summary>Angaben des UserInfo-Endpunkts als Text je Claim; null bei Fehler.</summary>
    Task<IReadOnlyDictionary<string, string>?> GetUserInfoAsync(OAuthSettings settings, string accessToken, CancellationToken ct);
}

/// <summary>Beginn einer Anmeldung: Adresse beim Anbieter und was bis zur Rückkehr im Browser (Cookie) aufbewahrt wird.</summary>
public sealed record OAuthStart(string AuthorizeUrl, string State, string Verifier);

/// <summary>Erfolgreiche Anmeldung: Geräte-Token für das Sitzungs-Cookie.</summary>
public sealed record OAuthSignIn(string Token, string UserName, bool IsAdmin);

/// <summary>
/// Anmeldung über einen externen Identitätsanbieter (Organisationseinstellungen → Anmeldung). Ablauf: Authorization Code (RFC 6749 §4.1)
/// mit PKCE S256 (RFC 7636) und <c>state</c> gegen CSRF; der Code wird serverseitig mit Client-Secret eingetauscht, der Benutzer kommt
/// vom UserInfo-Endpunkt. Beim ersten Anmelden verknüpft der Server das <c>sub</c> des Anbieters mit dem Konto gleichen Namens (oder legt
/// es an, wenn erlaubt); danach zählt nur noch diese Verknüpfung, damit niemand über einen umbenannten Anbieter-Account ein fremdes Konto
/// übernimmt.
/// </summary>
public sealed class ExternalSignInService(IOAuthStore store, IOAuthProvider provider, IAccountStore accounts, AccountService accountService)
{
    private const int MaxText = 512;

    public async Task<OAuthSettings> SettingsAsync(CancellationToken ct) => await store.LoadAsync(ct) ?? new OAuthSettings();

    public async Task<OAuthLoginInfo> LoginInfoAsync(CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        return new OAuthLoginInfo(IsUsable(settings), settings.DisplayName);
    }

    public async Task<ServiceResult<OAuthSettingsInfo>> GetAsync(Principal caller, string redirectUri, CancellationToken ct) =>
        caller.IsAdmin ? ServiceResult.Ok(Describe(await SettingsAsync(ct), redirectUri)) : Forbidden<OAuthSettingsInfo>();

    public async Task<ServiceResult<OAuthSettingsInfo>> SaveAsync(
        Principal caller, OAuthSettingsRequest request, string redirectUri, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<OAuthSettingsInfo>();
        }

        var current = await SettingsAsync(ct);
        var settings = new OAuthSettings(
            request.Enabled,
            Clean(request.DisplayName),
            Clean(request.Issuer).TrimEnd('/'),
            Clean(request.AuthorizationEndpoint),
            Clean(request.TokenEndpoint),
            Clean(request.UserInfoEndpoint),
            Clean(request.ClientId),
            request.ClientSecret switch { null => current.ClientSecret, "" => null, var secret => secret },
            Clean(request.Scopes) is { Length: > 0 } scopes ? scopes : "openid profile email",
            Clean(request.UserNameClaim) is { Length: > 0 } claim ? claim : "preferred_username",
            request.CreateUsers);

        if (settings.Issuer.Length > 0 && (settings.AuthorizationEndpoint.Length == 0 || settings.TokenEndpoint.Length == 0 || settings.UserInfoEndpoint.Length == 0))
        {
            if (!IsAllowedUrl(settings.Issuer) || await provider.DiscoverAsync(settings.Issuer, ct) is not { } found)
            {
                return Fail<OAuthSettingsInfo>("Die Anbieter-Adresse liefert keine OpenID-Konfiguration (.well-known/openid-configuration).");
            }

            settings = settings with
            {
                AuthorizationEndpoint = settings.AuthorizationEndpoint.Length > 0 ? settings.AuthorizationEndpoint : found.Authorization,
                TokenEndpoint = settings.TokenEndpoint.Length > 0 ? settings.TokenEndpoint : found.Token,
                UserInfoEndpoint = settings.UserInfoEndpoint.Length > 0 ? settings.UserInfoEndpoint : found.UserInfo,
            };
        }

        string[] endpoints = [settings.AuthorizationEndpoint, settings.TokenEndpoint, settings.UserInfoEndpoint];
        if (endpoints.Any(e => e.Length > 0 && !IsAllowedUrl(e)))
        {
            return Fail<OAuthSettingsInfo>("Endpunkte müssen mit https:// beginnen (http nur für localhost).");
        }

        if (settings.Enabled && (endpoints.Any(e => e.Length == 0) || settings.ClientId.Length == 0 || settings.DisplayName.Length == 0))
        {
            return Fail<OAuthSettingsInfo>("Zum Einschalten fehlen Name, Client-ID oder Endpunkte.");
        }

        await store.SaveAsync(settings, ct);
        return ServiceResult.Ok(Describe(settings, redirectUri));
    }

    /// <summary>Adresse beim Anbieter mit frischem <c>state</c> und PKCE-Verifier; null, wenn die Anmeldung über den Anbieter aus ist.</summary>
    public async Task<OAuthStart?> BeginAsync(string redirectUri, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        if (!IsUsable(settings))
        {
            return null;
        }

        var (state, verifier) = (Random(), Random());
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = string.Join('&', new[]
        {
            ("response_type", "code"), ("client_id", settings.ClientId), ("redirect_uri", redirectUri), ("scope", settings.Scopes),
            ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"),
        }.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));
        var separator = settings.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return new OAuthStart(settings.AuthorizationEndpoint + separator + query, state, verifier);
    }

    /// <summary>Rückkehr vom Anbieter: prüft <c>state</c>, tauscht den Code, findet oder verknüpft das Konto und meldet ein Gerät an.</summary>
    public async Task<ServiceResult<OAuthSignIn>> CompleteAsync(
        string? code, string? state, string? error, string? expectedState, string? verifier, string redirectUri, string deviceName,
        CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        if (!IsUsable(settings))
        {
            return Fail<OAuthSignIn>(OAuthRefusal.Off);
        }

        if (error is not null)
        {
            return Fail<OAuthSignIn>(OAuthRefusal.Cancelled);
        }

        if (code is null || state is null || expectedState is null || verifier is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
        {
            return Fail<OAuthSignIn>(OAuthRefusal.Expired);
        }

        if (await provider.ExchangeCodeAsync(settings, code, redirectUri, verifier, ct) is not { } accessToken
            || await provider.GetUserInfoAsync(settings, accessToken, ct) is not { } claims)
        {
            return Fail<OAuthSignIn>(OAuthRefusal.Provider);
        }

        if (!claims.TryGetValue("sub", out var subject) || subject.Length == 0
            || !claims.TryGetValue(settings.UserNameClaim, out var name) || name.Trim().Length == 0)
        {
            return Fail<OAuthSignIn>(OAuthRefusal.NoName);
        }

        var providerKey = ProviderKey(settings);
        var (user, refusal) = await store.FindLinkedUserAsync(providerKey, subject, ct) is { } linkedId
            ? (await accounts.FindUserByIdAsync(linkedId, ct), OAuthRefusal.Gone)
            : await LinkByNameAsync(settings, providerKey, subject, name.Trim(), ct);
        if (user is null)
        {
            return Fail<OAuthSignIn>(refusal!);
        }

        if (user.Disabled)
        {
            return Fail<OAuthSignIn>(OAuthRefusal.Locked);
        }

        var device = await accountService.IssueDeviceAsync(user.Id, deviceName, "web", ProductVersion.Current, ct);
        return device.IsSuccess
            ? ServiceResult.Ok(new OAuthSignIn(device.Value!.Token, user.Name, user.IsAdmin))
            : ServiceResult.Fail<OAuthSignIn>(device.Error!);
    }

    /// <summary>Erste Anmeldung: Konto gleichen Namens (noch ohne Verknüpfung zu diesem Anbieter) oder ein neues, wenn erlaubt.</summary>
    private async Task<(UserRecord? User, string? Refusal)> LinkByNameAsync(
        OAuthSettings settings, string providerKey, string subject, string name, CancellationToken ct)
    {
        var user = await accounts.FindUserByNameAsync(name, ct);
        if (user is null && settings.CreateUsers)
        {
            // Zufallspasswort, das niemand kennt: das Konto meldet sich nur über den Anbieter an, bis ein Administrator eines setzt
            var created = await accountService.CreateUserAsync(name, Random() + Random(), isAdmin: false, ct);
            user = created.IsSuccess ? await accounts.FindUserByIdAsync(created.Value!, ct) : null;
        }

        if (user is null)
        {
            return (null, OAuthRefusal.NoAccount);
        }

        if (await store.FindSubjectAsync(providerKey, user.Id, ct) is not null)
        {
            return (null, OAuthRefusal.LinkedElsewhere);
        }

        await store.LinkAsync(providerKey, subject, user.Id, ct);
        return (user, null);
    }

    /// <summary>Kennung des Anbieters für Verknüpfungen: der Issuer, sonst Schema und Host des Autorisierungsendpunkts.</summary>
    private static string ProviderKey(OAuthSettings settings) =>
        settings.Issuer.Length > 0 ? settings.Issuer : new Uri(settings.AuthorizationEndpoint).GetLeftPart(UriPartial.Authority);

    private static bool IsUsable(OAuthSettings s) =>
        s.Enabled && s.ClientId.Length > 0 && s.AuthorizationEndpoint.Length > 0 && s.TokenEndpoint.Length > 0 && s.UserInfoEndpoint.Length > 0;

    /// <summary>https, bei Tests und Anbietern auf demselben Rechner auch http://localhost.</summary>
    internal static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static OAuthSettingsInfo Describe(OAuthSettings s, string redirectUri) => new(
        s.Enabled, s.DisplayName, s.Issuer, s.AuthorizationEndpoint, s.TokenEndpoint, s.UserInfoEndpoint, s.ClientId,
        !string.IsNullOrEmpty(s.ClientSecret), s.Scopes, s.UserNameClaim, s.CreateUsers, redirectUri);

    private static string Clean(string? value) => (value ?? string.Empty).Trim() is var text && text.Length <= MaxText ? text : text[..MaxText];

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ServiceResult<T> Forbidden<T>() => ServiceResult.Fail<T>(new ServiceError(ErrorCode.Forbidden, "Nur für Administratoren."));

    private static ServiceResult<T> Fail<T>(string message) => ServiceResult.Fail<T>(new ServiceError(ErrorCode.InvalidRequest, message));
}
