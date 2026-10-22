using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>
/// „Notification Center“ (Glocke in der linken Leiste): neueste zuerst, ungelesene hervorgehoben und gezählt. Ein Klick markiert gelesen;
/// wohin er führt (geteilter Space), entscheidet die Oberfläche. Neue Einträge holt ein ruhiges Abfragen nach.
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

    /// <summary>
    /// Holt in Abständen neue Benachrichtigungen, solange die Seite offen ist. Fehler (kurz offline) bleiben still, damit keine Meldung
    /// aufblinkt, die der Nutzer nicht ausgelöst hat; nur eine abgelaufene Sitzung meldet sich ab.
    /// </summary>
    public async Task RunPollingAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Items = await api.ListNotificationsAsync(ct);
                State = Items.Count == 0 ? ListState.Empty : ListState.Ready;
                OnChanged();
            }
            catch (DriveException e) when (e.Code == ErrorCode.Unauthorized)
            {
                OnSignedOut();
                return;
            }
            catch (Exception e) when (e is DriveException or HttpRequestException)
            {
                // beim nächsten Durchlauf wieder
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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

    public string UsedText => string.Format(CultureInfo.CurrentCulture, Texts.StorageUsed, WebTexts.FormatSize(UsedBytes ?? 0));

    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = [];

    public ListState DevicesState { get; private set; } = ListState.Loading;

    public DeviceInfo? SelectedDevice { get; private set; }

    public bool CanUnlink => SelectedDevice is { IsCurrent: false };

    public async Task LoadUsageAsync(IReadOnlyList<SpaceSummary> spaces, CancellationToken ct)
    {
        long total = 0;
        if (await RunAsync(async () =>
            {
                foreach (var space in spaces.Where(s => s.Role == "owner"))
                {
                    total += (await api.UsageAsync(space.Id, ct)).Bytes;
                }
            }))
        {
            UsedBytes = total;
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
