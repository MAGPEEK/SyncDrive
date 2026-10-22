using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Sharing;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Items;

/// <summary>Benachrichtigungen (Tray-Reiter „Notifications“, Web „Notification Center“). Art: Freigabe an den Empfänger.</summary>
public sealed class NotificationService(INotificationStore store, SharingService sharing, AccountService accounts, TimeProvider clock)
{
    /// <summary>Jemand hat einen Space oder Ordner mit dem Empfänger geteilt; im Client „Person shared Ordner“.</summary>
    public const string Shared = "shared";
    private const int DefaultLimit = 100;

    /// <summary>
    /// Nach „Mitglied hinzufügen“ oder einer Einladung: der neue Teilnehmer erfährt, wer was mit ihm geteilt hat (nicht, wenn er sich selbst
    /// hinzufügt). <paramref name="spaceId"/> ist die Id, unter der der Empfänger es sieht (Space oder Einladung).
    /// </summary>
    public async Task SharedAsync(Principal caller, string spaceId, string recipientUserName, CancellationToken ct)
    {
        if (await accounts.UserIdOfAsync(recipientUserName, ct) is not { } recipient || recipient == caller.UserId)
        {
            return;
        }

        await store.AddAsync(new NotificationRecord(
            "nt_" + Guid.NewGuid().ToString("N"), recipient, Shared, clock.GetUtcNow(), caller.UserId, spaceId, null, string.Empty, Read: false), ct);
    }

    public async Task<NotificationsResponse> ListAsync(Principal caller, CancellationToken ct)
    {
        var result = new List<NotificationInfo>();
        foreach (var n in await store.ListAsync(caller.UserId, DefaultLimit, ct))
        {
            var (spaceId, path) = (n.SpaceId, n.Path);
            if (n.NodeId is not null && spaceId is not null && path is not null
                && await sharing.LocateAsync(caller.UserId, spaceId, path, ct) is { } located)
            {
                (spaceId, path) = located;
            }

            result.Add(new NotificationInfo(n.Id, n.Type, n.CreatedAt, await accounts.UserNameOfAsync(n.ActorUserId, ct), spaceId, path, n.Read));
        }

        return new NotificationsResponse(result);
    }

    public Task<bool> MarkReadAsync(Principal caller, string notificationId, CancellationToken ct) =>
        store.MarkReadAsync(caller.UserId, notificationId, ct);
}
