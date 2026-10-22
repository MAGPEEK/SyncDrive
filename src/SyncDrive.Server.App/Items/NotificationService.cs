using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Sharing;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Items;

/// <summary>Benachrichtigungen (Tray-Reiter „Notifications“, Web „Notification Center“). Erste Art: Bitte um Entsperren (A15).</summary>
public sealed class NotificationService(INotificationStore store, SharingService sharing, AccountService accounts, TimeProvider clock)
{
    public const string UnlockRequest = "unlock_request";

    /// <summary>Jemand hat einen Space oder Ordner mit dem Empfänger geteilt; im Client „Person shared Ordner“.</summary>
    public const string Shared = "shared";
    private const int DefaultLimit = 100;

    /// <summary>
    /// „Entsperren anfragen“: benachrichtigt den Benutzer, der die Datei gesperrt hat. Gemerkt werden Space und Pfad im Space selbst; beim
    /// Lesen rechnet <see cref="ListAsync"/> sie in den Weg um, auf dem der Empfänger die Datei sieht (ganzer Space oder geteilter Ordner).
    /// </summary>
    public async Task<ServiceError?> RequestUnlockAsync(Principal caller, string spaceId, string path, CancellationToken ct)
    {
        var view = await sharing.OpenAsync(caller, spaceId, SpaceRole.Viewer, ct);
        if (!view.IsSuccess)
        {
            return view.Error;
        }

        var node = await view.Value!.GetNodeAsync(path, ct);
        if (!node.IsSuccess || node.Value!.Node.Deleted)
        {
            return node.Error ?? new ServiceError(ErrorCode.NotFound, "Datei existiert nicht.");
        }

        if (node.Value.Node.LockedBy is not { } owner || owner == caller.UserId)
        {
            return new ServiceError(ErrorCode.InvalidRequest, "Die Datei ist nicht von jemand anderem gesperrt.");
        }

        await store.AddAsync(new NotificationRecord(
            "nt_" + Guid.NewGuid().ToString("N"), owner, UnlockRequest, clock.GetUtcNow(), caller.UserId,
            view.Value.SpaceId, node.Value.Node.NodeId, view.Value.Inner(path), Read: false), ct);
        return null;
    }

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
