using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// Admin-Konsole, Reiter „Benutzer“ (M7): Liste mit Rolle, Status, Speicher und Kontingent, Geräten; anlegen, sperren und entsperren,
/// Admin-Recht, Kontingent, Passwort neu. Das eigene Konto lässt sich weder sperren noch herabstufen (der Server prüft das ebenfalls).
/// </summary>
public sealed class AdminViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public const int MinPasswordLength = 10;
    private const long BytesPerGb = 1024L * 1024 * 1024;

    public ListState State { get; private set; } = ListState.Loading;

    public IReadOnlyList<AdminUserInfo> Users { get; private set; } = [];

    public AdminUserInfo? Selected { get; private set; }

    /// <summary>Der angemeldete Administrator (aus der Sitzung), damit die Oberfläche das eigene Konto schützt.</summary>
    public string? Me { get; set; }

    public bool IsMe(AdminUserInfo user) => string.Equals(user.Name, Me, StringComparison.OrdinalIgnoreCase);

    public bool CanToggle => Selected is { } user && !IsMe(user);

    public async Task LoadAsync(CancellationToken ct)
    {
        IReadOnlyList<AdminUserInfo> users = [];
        await RunAsync(async () => users = await api.ListUsersAsync(ct), Explain);
        Users = users;
        Selected = Users.FirstOrDefault(u => u.Id == Selected?.Id);
        State = Users.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }

    public void Select(AdminUserInfo user)
    {
        Selected = user;
        OnChanged();
    }

    /// <summary>„1,2 GB von 5 GB“ bzw. „1,2 GB · unbegrenzt“.</summary>
    public string StorageText(AdminUserInfo user) => user.QuotaBytes is { } quota
        ? string.Format(CultureInfo.CurrentCulture, Texts.UsedOf, WebTexts.FormatSize(user.UsedBytes), WebTexts.FormatSize(quota))
        : WebTexts.FormatSize(user.UsedBytes) + " · " + Texts.Unlimited;

    /// <summary>Anteil des Kontingents für den Balken; null ohne Kontingent.</summary>
    public static double? StorageShare(AdminUserInfo user) => user.QuotaBytes switch
    {
        null => null,
        <= 0 => 1,
        var quota => Math.Min(1, (double)user.UsedBytes / quota.Value),
    };

    public static double? QuotaInGb(AdminUserInfo user) => user.QuotaBytes is { } quota ? Math.Round((double)quota / BytesPerGb, 2) : null;

    /// <param name="quotaGb">Kontingent in GB; null = unbegrenzt.</param>
    public async Task<bool> CreateAsync(string name, string password, bool isAdmin, double? quotaGb, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail(Texts.InvalidName);
        }

        if (password.Length < MinPasswordLength)
        {
            return Fail(Texts.UserPasswordTooShort);
        }

        if (quotaGb is < 0)
        {
            return Fail(Texts.InvalidQuota);
        }

        AdminUserInfo? created = null;
        var ok = await RunAsync(async () => created = await api.CreateUserAsync(new CreateUserRequest(name.Trim(), password, isAdmin, Bytes(quotaGb)), ct), Explain);
        await LoadAsync(ct);
        if (ok)
        {
            Selected = Users.FirstOrDefault(u => u.Id == created!.Id);
            Inform(string.Format(CultureInfo.CurrentCulture, Texts.UserCreated, created!.Name));
        }

        return ok;
    }

    public Task<bool> SetDisabledAsync(AdminUserInfo user, bool disabled, CancellationToken ct) =>
        IsMe(user) ? Task.FromResult(Fail(Texts.CannotChangeSelf)) : UpdateAsync(user, new UpdateUserRequest(Disabled: disabled), ct);

    /// <summary>Einstellungen eines Benutzers: Admin-Recht und Kontingent (null = unbegrenzt).</summary>
    public Task<bool> SaveSettingsAsync(AdminUserInfo user, bool isAdmin, double? quotaGb, CancellationToken ct)
    {
        if (quotaGb is < 0)
        {
            return Task.FromResult(Fail(Texts.InvalidQuota));
        }

        if (IsMe(user) && !isAdmin)
        {
            return Task.FromResult(Fail(Texts.CannotChangeSelf));
        }

        return UpdateAsync(user, new UpdateUserRequest(IsAdmin: isAdmin, QuotaBytes: Bytes(quotaGb), ClearQuota: quotaGb is null), ct);
    }

    public async Task<bool> SetPasswordAsync(AdminUserInfo user, string password, CancellationToken ct)
    {
        if (password.Length < MinPasswordLength)
        {
            return Fail(Texts.UserPasswordTooShort);
        }

        var ok = await RunAsync(() => api.SetUserPasswordAsync(user.Id, password, ct), Explain);
        await LoadAsync(ct);
        if (ok)
        {
            Inform(string.Format(CultureInfo.CurrentCulture, Texts.PasswordReset, user.Name));
        }

        return ok;
    }

    private async Task<bool> UpdateAsync(AdminUserInfo user, UpdateUserRequest request, CancellationToken ct)
    {
        var ok = await RunAsync(() => api.UpdateUserAsync(user.Id, request, ct), Explain);
        await LoadAsync(ct);
        if (ok)
        {
            Inform(Texts.Saved);
        }

        return ok;
    }

    private static long? Bytes(double? gb) => gb is { } value ? (long)Math.Round(value * BytesPerGb) : null;

    private string? Explain(DriveException e) => e.Code switch
    {
        ErrorCode.AlreadyExists => Texts.UserExists,
        ErrorCode.Forbidden => Texts.NoPermission,
        ErrorCode.InvalidRequest => string.Format(CultureInfo.CurrentCulture, Texts.GenericError, e.Message),
        _ => null,
    };
}
