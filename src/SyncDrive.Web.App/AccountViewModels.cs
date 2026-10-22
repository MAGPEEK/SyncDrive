using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// „Notification Center“ (Glocke in der linken Leiste): neueste zuerst, ungelesene hervorgehoben und gezählt. Ein Klick markiert gelesen;
/// wohin er führt (geteilter Space), entscheidet die Oberfläche. Neue Einträge kommen über die Echtzeit (<see cref="LiveUpdatesViewModel"/>).
/// </summary>
public sealed class NotificationsViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public const string Shared = "shared";

    public IReadOnlyList<NotificationInfo> Items { get; private set; } = [];

    public ListState State { get; private set; } = ListState.Loading;

    public int Unread => Items.Count(n => !n.Read);

    public async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync(async () => Items = await api.ListNotificationsAsync(ct));
        State = Items.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }

    /// <summary>„ben hat „Familie“ mit Ihnen geteilt“; <paramref name="spaceName"/> übersetzt die Space-Id in den angezeigten Namen.</summary>
    public string TextOf(NotificationInfo notification, Func<string?, string> spaceName)
    {
        var actor = notification.Actor ?? "?";
        return notification.Type switch
        {
            Shared => string.Format(CultureInfo.CurrentCulture, Texts.NotifyShared, actor, spaceName(notification.SpaceId)),
            _ => notification.Type,
        };
    }

    public async Task MarkReadAsync(NotificationInfo notification, CancellationToken ct)
    {
        if (notification.Read || !await RunAsync(() => api.MarkNotificationReadAsync(notification.Id, ct)))
        {
            return;
        }

        Items = [.. Items.Select(n => n.Id == notification.Id ? n with { Read = true } : n)];
        OnChanged();
    }

    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        foreach (var notification in Items.Where(n => !n.Read))
        {
            if (!await RunAsync(() => api.MarkNotificationReadAsync(notification.Id, ct)))
            {
                break;
            }
        }

        await LoadAsync(ct);
    }
}

/// <summary>
/// Konto-Menü: belegter Speicher im Kopf und die „Client list“ mit „Unlink“ und „Refresh“. Das eigene Browser-Gerät lässt sich hier nicht
/// trennen; dafür gibt es „Abmelden“.
/// </summary>
public sealed class AccountViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    /// <summary>Summe der Spaces, die dem Benutzer gehören; null, solange unbekannt.</summary>
    public long? UsedBytes { get; private set; }

    /// <summary>Vom Administrator gesetztes Kontingent; null = unbegrenzt.</summary>
    public long? QuotaBytes { get; private set; }

    /// <summary>„1,2 GB von 5 GB belegt“ mit Kontingent, sonst „1,2 GB belegt“.</summary>
    public string UsedText => QuotaBytes is { } quota
        ? string.Format(CultureInfo.CurrentCulture, Texts.StorageUsedOf, WebTexts.FormatSize(UsedBytes ?? 0), WebTexts.FormatSize(quota))
        : string.Format(CultureInfo.CurrentCulture, Texts.StorageUsed, WebTexts.FormatSize(UsedBytes ?? 0));

    /// <summary>Anteil am Kontingent (0–1) für den Balken; null ohne Kontingent.</summary>
    public double? QuotaShare => QuotaBytes switch
    {
        null => null,
        0 => 1,
        var quota => Math.Min(1, (double)(UsedBytes ?? 0) / quota.Value),
    };

    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = [];

    public ListState DevicesState { get; private set; } = ListState.Loading;

    public DeviceInfo? SelectedDevice { get; private set; }

    public bool CanUnlink => SelectedDevice is { IsCurrent: false };

    public const int MaxAvatarBytes = 5 * 1024 * 1024;
    private int _avatarVersion;

    /// <summary>Adresse des eigenen Profilbilds; wechselt nach jedem Hochladen, damit der Browser das neue lädt.</summary>
    public string AvatarUrl => $"api/v1/account/avatar?v={_avatarVersion}";

    /// <summary>Kein Bild (der Server antwortete 404 oder es wurde entfernt): dann zeigt die Oberfläche den Anfangsbuchstaben.</summary>
    public bool HasAvatar { get; private set; } = true;

    public void AvatarMissing()
    {
        HasAvatar = false;
        OnChanged();
    }

    public async Task<bool> SetAvatarAsync(UploadFile image, CancellationToken ct)
    {
        if (image.Size > MaxAvatarBytes)
        {
            return Fail(Texts.ImageTooLarge);
        }

        var ok = await RunAsync(async () =>
        {
            await using var content = image.Open();
            await api.SetAvatarAsync(content, ct);
        });
        if (ok)
        {
            _avatarVersion++;
            HasAvatar = true;
        }

        OnChanged();
        return ok;
    }

    public async Task RemoveAvatarAsync(CancellationToken ct)
    {
        if (await RunAsync(() => api.RemoveAvatarAsync(ct)))
        {
            _avatarVersion++;
            HasAvatar = false;
        }

        OnChanged();
    }

    public async Task LoadStorageAsync(CancellationToken ct)
    {
        StorageInfo? storage = null;
        if (await RunAsync(async () => storage = await api.GetStorageAsync(ct)))
        {
            (UsedBytes, QuotaBytes) = (storage!.UsedBytes, storage.QuotaBytes);
        }

        OnChanged();
    }

    /// <summary>Zuletzt verbundene zuerst.</summary>
    public async Task LoadDevicesAsync(CancellationToken ct)
    {
        DevicesState = ListState.Loading;
        OnChanged();
        IReadOnlyList<DeviceInfo> devices = [];
        await RunAsync(async () => devices = await api.ListDevicesAsync(ct));
        Devices = [.. devices.OrderByDescending(d => d.LastSeen ?? d.CreatedAt)];
        SelectedDevice = Devices.FirstOrDefault(d => d.DeviceId == SelectedDevice?.DeviceId);
        DevicesState = Devices.Count == 0 ? ListState.Empty : ListState.Ready;
        OnChanged();
    }

    public void SelectDevice(DeviceInfo device)
    {
        SelectedDevice = device;
        OnChanged();
    }

    public string ClientTypeOf(DeviceInfo device) => device.ClientType switch
    {
        "web" => Texts.ClientWeb,
        "desktop" => Texts.ClientDesktop,
        { } other => other,
        null => "—",
    };

    public string UnlinkQuestion => string.Format(CultureInfo.CurrentCulture, Texts.ConfirmUnlink, SelectedDevice?.Name);

    /// <summary>Trennt das gewählte Gerät: sein Token gilt nicht mehr, es muss sich neu anmelden.</summary>
    public async Task<bool> UnlinkSelectedAsync(CancellationToken ct)
    {
        if (SelectedDevice is not { IsCurrent: false } device)
        {
            return false;
        }

        var ok = await RunAsync(() => api.UnlinkDeviceAsync(device.DeviceId, ct));
        await LoadDevicesAsync(ct);
        if (ok)
        {
            Inform(string.Format(CultureInfo.CurrentCulture, Texts.DeviceUnlinked, device.Name));
        }

        return ok;
    }
}
