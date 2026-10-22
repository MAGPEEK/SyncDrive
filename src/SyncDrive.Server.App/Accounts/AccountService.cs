using System.Security.Cryptography;
using System.Text;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Accounts;

/// <summary>Der angemeldete Aufrufer, wie ihn <see cref="AccountService.AuthenticateAsync"/> liefert.</summary>
public sealed record Principal(string UserId, string DeviceId, bool IsAdmin);

/// <summary>Benutzer, Geräte-Token und Zugriffsrechte auf Spaces.</summary>
public sealed class AccountService(IAccountStore store, IPasswordHasher hasher, TimeProvider clock)
{
    public const int MinPasswordLength = 10;
    private const int MaxNameLength = 64;
    private const string TokenPrefix = "sd_";
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(5);

    // Wird verifiziert, wenn der Benutzer fehlt, damit die Antwortzeit nichts über seine Existenz verrät.
    private readonly Lazy<string> _decoyHash = new(() => hasher.Hash("decoy-" + Guid.NewGuid()));

    public Task<ServiceResult<string>> CreateUserAsync(string name, string password, bool isAdmin, CancellationToken ct) =>
        CreateUserAsync(name, password, isAdmin, quotaBytes: null, ct);

    /// <param name="quotaBytes">Speicherkontingent der eigenen Spaces; null = unbegrenzt.</param>
    public async Task<ServiceResult<string>> CreateUserAsync(
        string name, string password, bool isAdmin, long? quotaBytes, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            return Fail<string>(ErrorCode.InvalidRequest, "Benutzername ungültig.");
        }

        if (password.Length < MinPasswordLength)
        {
            return Fail<string>(ErrorCode.InvalidRequest, $"Passwort muss mindestens {MinPasswordLength} Zeichen haben.");
        }

        if (await store.FindUserByNameAsync(name, ct) is not null)
        {
            return Fail<string>(ErrorCode.AlreadyExists, "Benutzer existiert bereits.");
        }

        var userId = "u_" + Guid.NewGuid().ToString("N");
        await store.AddUserAsync(new UserRecord(userId, name, hasher.Hash(password), isAdmin, false, quotaBytes, clock.GetUtcNow()), ct);
        await store.CreateSpaceAsync("sp_" + Guid.NewGuid().ToString("N"), "Meine Dateien", userId, ct);
        return ServiceResult.Ok(userId);
    }

    /// <summary>Neues Passwort (Admin setzt es zurück); alle Geräte des Benutzers müssen sich danach neu anmelden.</summary>
    public async Task<ServiceError?> SetPasswordAsync(string userId, string password, CancellationToken ct)
    {
        if (password.Length < MinPasswordLength)
        {
            return new ServiceError(ErrorCode.InvalidRequest, $"Passwort muss mindestens {MinPasswordLength} Zeichen haben.");
        }

        if (await store.FindUserByIdAsync(userId, ct) is not { } user)
        {
            return new ServiceError(ErrorCode.NotFound, "Benutzer unbekannt.");
        }

        await store.UpdateUserAsync(user with { PasswordHash = hasher.Hash(password) }, ct);
        await store.RevokeAllDevicesAsync(userId, ct);
        return null;
    }

    public async Task<ServiceResult<DeviceRegistrationResponse>> RegisterDeviceAsync(
        DeviceRegistrationRequest request, CancellationToken ct)
    {
        var deviceName = request.DeviceName?.Trim() ?? string.Empty;
        if (deviceName.Length is 0 or > MaxNameLength)
        {
            return Fail<DeviceRegistrationResponse>(ErrorCode.InvalidRequest, "Gerätename ungültig.");
        }

        var user = await store.FindUserByNameAsync(request.UserName ?? string.Empty, ct);
        var passwordOk = hasher.Verify(request.Password ?? string.Empty, user?.PasswordHash ?? _decoyHash.Value);
        if (user is null || user.Disabled || !passwordOk)
        {
            return Fail<DeviceRegistrationResponse>(ErrorCode.Unauthorized, "Anmeldung fehlgeschlagen.");
        }

        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var deviceId = "d_" + Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        await store.AddDeviceAsync(
            new DeviceRecord(deviceId, user.Id, deviceName, HashToken(token), now, false,
                Trimmed(request.ClientType), Trimmed(request.ClientVersion), now), ct);
        return ServiceResult.Ok(new DeviceRegistrationResponse(deviceId, token));
    }

    /// <summary>Geräte des Aufrufers (Web „Client list“); das eigene ist markiert.</summary>
    public async Task<DevicesResponse> ListDevicesAsync(Principal caller, CancellationToken ct) =>
        new([.. (await store.ListDevicesAsync(caller.UserId, ct)).Select(d =>
            new DeviceInfo(d.Id, d.Name, d.ClientType, d.ClientVersion, d.CreatedAt, d.LastSeen, d.Id == caller.DeviceId))]);

    /// <summary>Fügt einen Benutzer mit Rolle hinzu oder ändert sie. Nur Manager und Besitzer; niemand vergibt mehr Rechte, als er selbst hat.</summary>
    public async Task<ServiceError?> AddMemberAsync(Principal caller, string spaceId, AddMemberRequest request, CancellationToken ct)
    {
        if (await AuthorizeAsync(caller, spaceId, SpaceRole.Manager, ct) is { } denied)
        {
            return denied;
        }

        if (!Enum.TryParse<SpaceRole>(request.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role))
        {
            return new ServiceError(ErrorCode.InvalidRequest, "Unbekannte Rolle.");
        }

        var own = await store.GetRoleAsync(caller.UserId, spaceId, ct);
        if (role > own)
        {
            return new ServiceError(ErrorCode.Forbidden, "Höhere Rolle als die eigene kann nicht vergeben werden.");
        }

        if (await store.FindUserByNameAsync(request.UserName ?? string.Empty, ct) is not { } user)
        {
            return new ServiceError(ErrorCode.NotFound, "Benutzer unbekannt.");
        }

        await store.SetMemberAsync(spaceId, user.Id, role, ct);
        return null;
    }

    public Task<SpaceRole?> RoleOfAsync(Principal caller, string spaceId, CancellationToken ct) =>
        store.GetRoleAsync(caller.UserId, spaceId, ct);

    public async Task<string?> UserNameOfAsync(string? userId, CancellationToken ct) =>
        userId is null ? null : (await store.FindUserByIdAsync(userId, ct))?.Name;

    public async Task<string?> UserIdOfAsync(string userName, CancellationToken ct) => (await store.FindUserByNameAsync(userName, ct))?.Id;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, MaxNameLength)];

    public async Task<Principal?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (await store.FindDeviceByTokenHashAsync(HashToken(token), ct) is not { } found || found.Device.Revoked || found.User.Disabled)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (found.Device.LastSeen is not { } seen || now - seen > LastSeenResolution)
        {
            await store.TouchDeviceAsync(found.Device.Id, now, ct); // gedrosselt: nicht bei jeder Anfrage schreiben
        }

        return new Principal(found.User.Id, found.Device.Id, found.User.IsAdmin);
    }

    public Task<bool> RevokeDeviceAsync(Principal caller, string deviceId, CancellationToken ct) =>
        store.RevokeDeviceAsync(deviceId, caller.UserId, ct);

    public async Task<SpacesResponse> ListSpacesAsync(Principal caller, CancellationToken ct) =>
        new([.. (await store.ListMembershipsAsync(caller.UserId, ct))
            .Select(m => new SpaceSummary(m.SpaceId, m.SpaceName, m.Role.ToString().ToLowerInvariant()))]);

    /// <summary>Prüft die Mitgliedschaft. Fremde Spaces melden "nicht gefunden", damit ihre Existenz verborgen bleibt.</summary>
    public async Task<ServiceError?> AuthorizeAsync(
        Principal caller, string spaceId, SpaceRole needed, CancellationToken ct)
    {
        var role = await store.GetRoleAsync(caller.UserId, spaceId, ct);
        if (role is null)
        {
            return new ServiceError(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        return role >= needed ? null : new ServiceError(ErrorCode.Forbidden, "Keine Berechtigung.");
    }

    private static ServiceResult<T> Fail<T>(string code, string message) =>
        ServiceResult.Fail<T>(new ServiceError(code, message));

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
