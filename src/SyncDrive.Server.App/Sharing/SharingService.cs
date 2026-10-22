using System.Security.Cryptography;
using SyncDrive.Contracts;
using SyncDrive.Core.Paths;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Policies;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Sharing;

/// <summary>
/// Zugriff und Freigabe (M6b, ADR-12). Jede Anfrage auf einen Space geht durch <see cref="OpenAsync"/>: Mitglieder sehen den ganzen Space,
/// Eingeladene nur den geteilten Ordner unter der Id ihrer Einladung (<c>sh_…</c>). Freigeben darf, wer den Space mindestens verwaltet
/// (Manager); Eingeladene teilen nicht weiter. Öffentliche Links und Dateianfragen verwaltet derselbe Dienst, aufgelöst werden sie in
/// <see cref="PublicLinkService"/>.
/// </summary>
public sealed class SharingService(
    IShareStore shares, AccountService accounts, SpaceService spaces, IPasswordHasher hasher, TimeProvider clock, PolicyService policies)
{
    public const string SharePrefix = "sh_";
    public const int MinLinkPasswordLength = 4;

    private static readonly SpaceRole[] InviteRoles = [SpaceRole.Previewer, SpaceRole.Viewer, SpaceRole.Editor];

    /// <summary>Space oder geteilter Ordner unter der Id, mit der der Aufrufer ihn kennt. Fremdes meldet „nicht gefunden“.</summary>
    public async Task<ServiceResult<SpaceView>> OpenAsync(Principal caller, string accessId, SpaceRole needed, CancellationToken ct)
    {
        if (accessId.StartsWith(SharePrefix, StringComparison.Ordinal))
        {
            return await shares.FindShareAsync(accessId, ct) is { } share && share.UserId == caller.UserId
                ? await OpenNodeAsync(share.Id, share.SpaceId, share.NodeId, share.Role, needed, ct)
                : Fail<SpaceView>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        return await accounts.RoleOfAsync(caller, accessId, ct) switch
        {
            null => Fail<SpaceView>(ErrorCode.SpaceNotFound, "Space unbekannt."),
            var role when role < needed => Fail<SpaceView>(ErrorCode.Forbidden, "Keine Berechtigung."),
            var role => ServiceResult.Ok(new SpaceView(spaces, accessId, accessId, RelativePath.Root, role.Value)),
        };
    }

    /// <summary>Ein Knoten als View mit fester Rolle: geteilter Ordner einer Einladung oder Ziel eines öffentlichen Links.</summary>
    internal async Task<ServiceResult<SpaceView>> OpenNodeAsync(
        string id, string spaceId, string nodeId, SpaceRole role, SpaceRole needed, CancellationToken ct)
    {
        var node = await spaces.GetNodeByIdAsync(spaceId, nodeId, ct);
        if (!node.IsSuccess || !RelativePath.TryParse(node.Value!.Path, out var root))
        {
            return Fail<SpaceView>(ErrorCode.SpaceNotFound, "Die Freigabe gibt es nicht mehr.");
        }

        return role < needed
            ? Fail<SpaceView>(ErrorCode.Forbidden, "Keine Berechtigung.")
            : ServiceResult.Ok(new SpaceView(spaces, id, spaceId, root, role));
    }

    /// <summary>Spaces des Benutzers und die mit ihm geteilten Ordner („Shared with me“), deren Ordner es noch gibt.</summary>
    public async Task<SpacesResponse> ListAccessesAsync(Principal caller, CancellationToken ct)
    {
        var list = new List<SpaceSummary>((await accounts.ListSpacesAsync(caller, ct)).Spaces);
        foreach (var share in await shares.ListSharesForUserAsync(caller.UserId, ct))
        {
            if (await spaces.GetNodeByIdAsync(share.SpaceId, share.NodeId, ct) is { IsSuccess: true } node)
            {
                list.Add(new SpaceSummary(
                    share.Id, NameOf(node.Value!.Path), RoleText(share.Role), await accounts.UserNameOfAsync(share.CreatedBy, ct) ?? "?"));
            }
        }

        return new SpacesResponse(list);
    }

    /// <summary>Freigabedialog: Eingeladene, öffentlicher Link und Dateianfrage eines Elements.</summary>
    public async Task<ServiceResult<SharingResponse>> GetSharingAsync(Principal caller, string accessId, string path, CancellationToken ct)
    {
        var target = await ManageAsync(caller, accessId, path, ct);
        if (!target.IsSuccess)
        {
            return ServiceResult.Fail<SharingResponse>(target.Error!);
        }

        var (view, node) = target.Value;
        return ServiceResult.Ok(await DescribeAsync(view.SpaceId, node.NodeId, ct));
    }

    /// <summary>Lädt einen Benutzer zu einem Ordner ein oder ändert seine Rolle; nicht sich selbst und niemanden, der den Space schon hat.</summary>
    public async Task<ServiceResult<InviteeInfo>> InviteAsync(
        Principal caller, string accessId, string path, InviteRequest request, CancellationToken ct)
    {
        var target = await ManageAsync(caller, accessId, path, ct);
        if (!target.IsSuccess)
        {
            return ServiceResult.Fail<InviteeInfo>(target.Error!);
        }

        var (view, node) = target.Value;
        if (node.Kind != NodeKinds.Directory)
        {
            return Fail<InviteeInfo>(ErrorCode.InvalidRequest, "Einladen geht nur für Ordner; Dateien teilt man per Link.");
        }

        if (!Enum.TryParse<SpaceRole>(request.Role, ignoreCase: true, out var role) || !InviteRoles.Contains(role))
        {
            return Fail<InviteeInfo>(ErrorCode.InvalidRequest, "Unbekannte Rolle.");
        }

        if (await accounts.UserIdOfAsync(request.UserName?.Trim() ?? string.Empty, ct) is not { } userId)
        {
            return Fail<InviteeInfo>(ErrorCode.NotFound, "Benutzer unbekannt.");
        }

        if (userId == caller.UserId || await accounts.RoleOfAsync(new Principal(userId, string.Empty, false), view.SpaceId, ct) is not null)
        {
            return Fail<InviteeInfo>(ErrorCode.AlreadyExists, "Der Benutzer hat schon Zugriff.");
        }

        var saved = await shares.SaveShareAsync(
            new ShareRecord(SharePrefix + Guid.NewGuid().ToString("N"), view.SpaceId, node.NodeId, userId, role, caller.UserId, clock.GetUtcNow()), ct);
        return ServiceResult.Ok(new InviteeInfo(saved.Id, await accounts.UserNameOfAsync(userId, ct) ?? "?", RoleText(saved.Role)));
    }

    /// <summary>Hebt eine Einladung auf: wer sie angelegt hat, wer den Space verwaltet, oder der Eingeladene selbst („Freigabe verlassen“).</summary>
    public async Task<ServiceError?> RemoveInviteAsync(Principal caller, string shareId, CancellationToken ct)
    {
        if (await shares.FindShareAsync(shareId, ct) is not { } share || !await MayRemoveAsync(caller, share.CreatedBy, share.SpaceId, ct)
            && share.UserId != caller.UserId)
        {
            return new ServiceError(ErrorCode.NotFound, "Einladung unbekannt.");
        }

        await shares.DeleteShareAsync(shareId, ct);
        return null;
    }

    /// <summary>
    /// Legt den öffentlichen Link (<see cref="LinkKinds.Share"/>) oder die Dateianfrage (<see cref="LinkKinds.Request"/>, nur Ordner) an oder
    /// ändert Ablauf und Passwort. Der Token bleibt beim Ändern derselbe, damit verschickte Links gültig bleiben.
    /// </summary>
    public async Task<ServiceResult<LinkInfo>> SetLinkAsync(
        Principal caller, string accessId, string path, string kind, LinkRequest request, CancellationToken ct)
    {
        if (kind is not (LinkKinds.Share or LinkKinds.Request))
        {
            return Fail<LinkInfo>(ErrorCode.InvalidRequest, "Unbekannte Link-Art.");
        }

        var target = await ManageAsync(caller, accessId, path, ct);
        if (!target.IsSuccess)
        {
            return ServiceResult.Fail<LinkInfo>(target.Error!);
        }

        var (view, node) = target.Value;
        if (kind == LinkKinds.Request && node.Kind != NodeKinds.Directory)
        {
            return Fail<LinkInfo>(ErrorCode.InvalidRequest, "Dateianfragen gehen nur für Ordner.");
        }

        if (request.ExpiresAt is { } expires && expires <= clock.GetUtcNow())
        {
            return Fail<LinkInfo>(ErrorCode.InvalidRequest, "Das Ablaufdatum liegt in der Vergangenheit.");
        }

        if (request.Password is { } password && password.Length < MinLinkPasswordLength)
        {
            return Fail<LinkInfo>(ErrorCode.InvalidRequest, $"Das Passwort braucht mindestens {MinLinkPasswordLength} Zeichen.");
        }

        var existing = (await shares.ListLinksOfNodeAsync(view.SpaceId, node.NodeId, ct)).FirstOrDefault(l => l.Kind == kind);
        var link = new LinkRecord(
            existing?.Id ?? "lk_" + Guid.NewGuid().ToString("N"),
            existing?.Token ?? NewToken(),
            kind,
            view.SpaceId,
            node.NodeId,
            existing?.CreatedBy ?? caller.UserId,
            existing?.CreatedAt ?? clock.GetUtcNow(),
            request.ExpiresAt,
            request.RemovePassword ? null : request.Password is { } newPassword ? hasher.Hash(newPassword) : existing?.PasswordHash);
        if (await policies.CheckLinkAsync(kind, link.PasswordHash is not null, link.ExpiresAt, clock.GetUtcNow(), ct) is { } violation)
        {
            return ServiceResult.Fail<LinkInfo>(violation);
        }

        await shares.SaveLinkAsync(link, ct);
        return ServiceResult.Ok(ToInfo(link));
    }

    public async Task<ServiceError?> RemoveLinkAsync(Principal caller, string linkId, CancellationToken ct)
    {
        if (await shares.FindLinkAsync(linkId, ct) is not { } link || !await MayRemoveAsync(caller, link.CreatedBy, link.SpaceId, ct))
        {
            return new ServiceError(ErrorCode.NotFound, "Link unbekannt.");
        }

        await shares.DeleteLinkAsync(linkId, ct);
        return null;
    }

    /// <summary>„Shared with others“: alles, was der Benutzer geteilt hat, je Element mit Eingeladenen und Links; Gelöschtes fällt weg.</summary>
    public async Task<SharedItemsResponse> ListSharedByMeAsync(Principal caller, CancellationToken ct)
    {
        var nodes = (await shares.ListSharesCreatedByAsync(caller.UserId, ct)).Select(s => (s.SpaceId, s.NodeId))
            .Concat((await shares.ListLinksCreatedByAsync(caller.UserId, ct)).Select(l => (l.SpaceId, l.NodeId)))
            .Distinct();
        var items = new List<SharedItem>();
        foreach (var (spaceId, nodeId) in nodes)
        {
            if (await spaces.GetNodeByIdAsync(spaceId, nodeId, ct) is { IsSuccess: true } node
                && await accounts.RoleOfAsync(caller, spaceId, ct) >= SpaceRole.Manager)
            {
                var sharing = await DescribeAsync(spaceId, nodeId, ct);
                items.Add(new SharedItem(spaceId, nodeId, node.Value!.Path, node.Value.Kind, sharing.Invitees, sharing.Link, sharing.Request));
            }
        }

        return new SharedItemsResponse([.. items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Unter welcher Space-Id und welchem Pfad sieht <paramref name="userId"/> einen Knoten des Space? Für Benachrichtigungen, die der
    /// Empfänger über einen anderen Weg erreicht als der Absender. Null, wenn er ihn gar nicht sieht.
    /// </summary>
    public async Task<(string AccessId, string Path)?> LocateAsync(string userId, string spaceId, string path, CancellationToken ct)
    {
        var user = new Principal(userId, string.Empty, false);
        if (await accounts.RoleOfAsync(user, spaceId, ct) is not null)
        {
            return (spaceId, path);
        }

        foreach (var share in (await shares.ListSharesForUserAsync(userId, ct)).Where(s => s.SpaceId == spaceId))
        {
            if (await OpenNodeAsync(share.Id, spaceId, share.NodeId, share.Role, SpaceRole.Previewer, ct) is { IsSuccess: true } view
                && view.Value!.Outer(path) is { } outer)
            {
                return (share.Id, outer);
            }
        }

        return null;
    }

    public static LinkInfo ToInfo(LinkRecord link) =>
        new(link.Id, link.Kind, link.Token, link.CreatedAt, link.ExpiresAt, link.PasswordHash is not null);

    public static string RoleText(SpaceRole role) => role.ToString().ToLowerInvariant();

    private async Task<SharingResponse> DescribeAsync(string spaceId, string nodeId, CancellationToken ct)
    {
        var invitees = new List<InviteeInfo>();
        foreach (var share in await shares.ListSharesOfNodeAsync(spaceId, nodeId, ct))
        {
            invitees.Add(new InviteeInfo(share.Id, await accounts.UserNameOfAsync(share.UserId, ct) ?? "?", RoleText(share.Role)));
        }

        var links = await shares.ListLinksOfNodeAsync(spaceId, nodeId, ct);
        return new SharingResponse(
            [.. invitees.OrderBy(i => i.UserName, StringComparer.OrdinalIgnoreCase)],
            links.FirstOrDefault(l => l.Kind == LinkKinds.Share) is { } share1 ? ToInfo(share1) : null,
            links.FirstOrDefault(l => l.Kind == LinkKinds.Request) is { } request ? ToInfo(request) : null);
    }

    /// <summary>Teilen setzt den ganzen Space voraus (Mitglied als Manager oder Besitzer); über eine Einladung wird nicht weitergeteilt.</summary>
    private async Task<ServiceResult<(SpaceView View, NodeInfo Node)>> ManageAsync(
        Principal caller, string accessId, string path, CancellationToken ct)
    {
        var view = await OpenAsync(caller, accessId, SpaceRole.Manager, ct);
        if (!view.IsSuccess)
        {
            return ServiceResult.Fail<(SpaceView, NodeInfo)>(view.Error!);
        }

        if (view.Value!.IsScoped)
        {
            return ServiceResult.Fail<(SpaceView, NodeInfo)>(new ServiceError(ErrorCode.Forbidden, "Geteilte Ordner werden nicht weitergeteilt."));
        }

        var node = await view.Value.GetNodeAsync(path, ct);
        if (!node.IsSuccess || node.Value!.Node.Deleted)
        {
            return ServiceResult.Fail<(SpaceView, NodeInfo)>(node.Error ?? new ServiceError(ErrorCode.NotFound, "Element existiert nicht."));
        }

        return ServiceResult.Ok((view.Value, node.Value.Node));
    }

    private async Task<bool> MayRemoveAsync(Principal caller, string createdBy, string spaceId, CancellationToken ct) =>
        createdBy == caller.UserId || await accounts.RoleOfAsync(caller, spaceId, ct) >= SpaceRole.Manager;

    /// <summary>128 Bit Zufall, URL-sicher (22 Zeichen).</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static ServiceResult<T> Fail<T>(string code, string message) => ServiceResult.Fail<T>(new ServiceError(code, message));
}
