using System.Net.Http.Headers;
using System.Text.Json;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Server.Host;

/// <summary>
/// HTTP zum Identitätsanbieter: Discovery, Code-Tausch (<c>client_secret_post</c>, mit PKCE-Verifier) und UserInfo. Antworten sind auf
/// 1 MB und 15 Sekunden begrenzt; Fehler werden zu null, die Anmeldeseite zeigt dann eine allgemeine Meldung.
/// </summary>
public sealed partial class HttpOAuthProvider(HttpClient http, ILogger<HttpOAuthProvider> log) : IOAuthProvider
{
    public const int MaxResponseBytes = 1024 * 1024;

    public async Task<(string Authorization, string Token, string UserInfo)?> DiscoverAsync(string issuer, CancellationToken ct)
    {
        using var document = await GetJsonAsync(new HttpRequestMessage(HttpMethod.Get, issuer.TrimEnd('/') + "/.well-known/openid-configuration"), ct);
        return document?.RootElement is { ValueKind: JsonValueKind.Object } root
            && Text(root, "authorization_endpoint") is { } authorization && Text(root, "token_endpoint") is { } token
            && Text(root, "userinfo_endpoint") is { } userInfo
            ? (authorization, token, userInfo)
            : null;
    }

    public async Task<string?> ExchangeCodeAsync(OAuthSettings settings, string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["client_id"] = settings.ClientId,
        };
        if (!string.IsNullOrEmpty(settings.ClientSecret))
        {
            form["client_secret"] = settings.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var document = await GetJsonAsync(request, ct);
        return document?.RootElement is { ValueKind: JsonValueKind.Object } root ? Text(root, "access_token") : null;
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetUserInfoAsync(OAuthSettings settings, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var document = await GetJsonAsync(request, ct);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root)
        {
            return null;
        }

        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                claims[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            }
        }

        return claims;
    }

    private async Task<JsonDocument?> GetJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                LogRefused(log, request.RequestUri?.GetLeftPart(UriPartial.Path), (int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogUnreachable(log, request.RequestUri?.GetLeftPart(UriPartial.Path), e.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identitätsanbieter {Uri} antwortete {Status}.")]
    private static partial void LogRefused(ILogger logger, string? uri, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identitätsanbieter {Uri} nicht erreichbar: {Error}")]
    private static partial void LogUnreachable(ILogger logger, string? uri, string error);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
