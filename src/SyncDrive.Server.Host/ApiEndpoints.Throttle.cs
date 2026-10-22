using System.Globalization;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.Host;

/// <summary>
/// Bremse für Passwortversuche (M7, Härtung): je Benutzer und Adresse eng, je Adresse weiter, je Benutzer über alle Adressen hoch. So bremst
/// sie das Durchprobieren, ohne dass ein Fremder den echten Benutzer mit ein paar Fehlversuchen aussperren kann.
/// </summary>
public static partial class ApiEndpoints
{
    private const int SignInPerUserAndAddress = 5;
    private const int SignInPerAddress = 30;
    private const int SignInPerUser = 50;
    private const int LinkPerAddress = 10;
    private const int LinkOverall = 100;

    private static AttemptKey[] SignInKeys(HttpContext http, string? userName)
    {
        var user = (userName ?? string.Empty).Trim().ToUpperInvariant();
        var address = Address(http);
        return
        [
            new("signin:user+address:" + user + "|" + address, SignInPerUserAndAddress),
            new("signin:address:" + address, SignInPerAddress),
            new("signin:user:" + user, SignInPerUser),
        ];
    }

    private static AttemptKey[] LinkKeys(HttpContext http, string linkId) =>
        [new("link:address:" + linkId + "|" + Address(http), LinkPerAddress), new("link:" + linkId, LinkOverall)];

    private static string Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";

    private static IResult TooManyAttempts(HttpContext http, TimeSpan retryAfter)
    {
        http.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return Problem(new ServiceError(ErrorCode.TooManyAttempts, "Zu viele Fehlversuche; bitte später erneut versuchen."));
    }

    /// <summary>
    /// Anmeldung mit Bremse: gesperrt → <c>RetryAfter</c> gesetzt, ohne das Passwort zu prüfen; ein falsches Passwort zählt, Erfolg setzt die
    /// Zähler des Benutzers zurück (der Zähler der Adresse bleibt, er bremst das Durchprobieren vieler Konten).
    /// </summary>
    private static async Task<(ServiceResult<DeviceRegistrationResponse> Result, TimeSpan? RetryAfter)> ThrottledRegisterAsync(
        HttpContext http, AccountService accounts, DeviceRegistrationRequest request, CancellationToken ct)
    {
        var limiter = http.RequestServices.GetRequiredService<AttemptLimiter>();
        var keys = SignInKeys(http, request.UserName);
        if (limiter.RetryAfter(keys) is { } wait)
        {
            return (default, wait);
        }

        var result = await accounts.RegisterDeviceAsync(request, ct);
        if (result.Error?.Code == ErrorCode.Unauthorized)
        {
            limiter.Failed(keys);
        }
        else if (result.IsSuccess)
        {
            limiter.Succeeded([keys[0], keys[2]]);
        }

        return (result, null);
    }
}
