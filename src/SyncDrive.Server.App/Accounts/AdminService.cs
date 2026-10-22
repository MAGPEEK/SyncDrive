using SyncDrive.Contracts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Accounts;

/// <summary>
/// Admin-Konsole (M7): Benutzer anlegen, sperren, Admin-Recht und Speicherkontingent setzen, Passwort neu vergeben. Nur für Administratoren.
/// Schutz vor dem Aussperren: man nimmt sich nicht selbst das Admin-Recht und sperrt sich nicht selbst; da nur aktive Administratoren
/// ändern dürfen, bleibt so immer einer übrig. Gesperrte Benutzer und neue Passwörter melden alle Geräte des Benutzers ab.
/// </summary>
public sealed partial class AdminService(
    IAccountStore store, IGroupStore groups, AccountService accounts, SpaceService spaces, Items.NotificationService notes)
{
    public async Task<ServiceResult<AdminUsersResponse>> ListUsersAsync(Principal caller, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<AdminUsersResponse>();
        }

        var users = new List<AdminUserInfo>();
        foreach (var user in await store.ListUsersAsync(ct))
        {
            users.Add(await DescribeAsync(user, ct));
        }

        return ServiceResult.Ok(new AdminUsersResponse(users));
    }

    public async Task<ServiceResult<AdminUserInfo>> CreateUserAsync(Principal caller, CreateUserRequest request, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<AdminUserInfo>();
        }

        if (request.QuotaBytes is < 0)
        {
            return Fail<AdminUserInfo>(ErrorCode.InvalidRequest, "Kontingent darf nicht negativ sein.");
        }

        var created = await accounts.CreateUserAsync(request.Name ?? string.Empty, request.Password ?? string.Empty, request.IsAdmin, request.QuotaBytes, ct);
        return created.IsSuccess
            ? ServiceResult.Ok(await DescribeAsync((await store.FindUserByIdAsync(created.Value!, ct))!, ct))
            : ServiceResult.Fail<AdminUserInfo>(created.Error!);
    }

    public async Task<ServiceResult<AdminUserInfo>> UpdateUserAsync(Principal caller, string userId, UpdateUserRequest request, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<AdminUserInfo>();
        }

        if (await store.FindUserByIdAsync(userId, ct) is not { } user)
        {
            return Fail<AdminUserInfo>(ErrorCode.NotFound, "Benutzer unbekannt.");
        }

        if (request.QuotaBytes is < 0)
        {
            return Fail<AdminUserInfo>(ErrorCode.InvalidRequest, "Kontingent darf nicht negativ sein.");
        }

        var updated = user with
        {
            IsAdmin = request.IsAdmin ?? user.IsAdmin,
            Disabled = request.Disabled ?? user.Disabled,
            QuotaBytes = request.ClearQuota ? null : request.QuotaBytes ?? user.QuotaBytes,
        };
        if (userId == caller.UserId && (!updated.IsAdmin || updated.Disabled))
        {
            return Fail<AdminUserInfo>(ErrorCode.InvalidRequest, "Das eigene Admin-Recht lässt sich nicht entziehen, das eigene Konto nicht sperren.");
        }

        await store.UpdateUserAsync(updated, ct);
        if (updated.Disabled && !user.Disabled)
        {
            await store.RevokeAllDevicesAsync(userId, ct); // gesperrt heißt: auch laufende Sitzungen enden
        }

        return ServiceResult.Ok(await DescribeAsync(updated, ct));
    }

    public async Task<ServiceError?> SetPasswordAsync(Principal caller, string userId, SetPasswordRequest request, CancellationToken ct) =>
        caller.IsAdmin
            ? await accounts.SetPasswordAsync(userId, request.Password ?? string.Empty, ct)
            : new ServiceError(ErrorCode.Forbidden, "Nur für Administratoren.");

    private async Task<AdminUserInfo> DescribeAsync(UserRecord user, CancellationToken ct)
    {
        long used = 0;
        foreach (var membership in (await store.ListMembershipsAsync(user.Id, ct)).Where(m => m.Role == SpaceRole.Owner))
        {
            used += await spaces.GetUsedBytesAsync(membership.SpaceId, ct);
        }

        var devices = (await store.ListDevicesAsync(user.Id, ct)).Count;
        return new AdminUserInfo(user.Id, user.Name, user.IsAdmin, user.Disabled, used, user.QuotaBytes, devices, user.CreatedAt);
    }

    private static ServiceResult<T> Forbidden<T>() => Fail<T>(ErrorCode.Forbidden, "Nur für Administratoren.");

    private static ServiceResult<T> Fail<T>(string code, string message) => ServiceResult.Fail<T>(new ServiceError(code, message));
}
