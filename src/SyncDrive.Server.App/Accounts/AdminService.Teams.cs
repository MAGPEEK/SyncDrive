using SyncDrive.Contracts;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Accounts;

/// <summary>
/// Gruppen und Team-Ordner (M7). Ein Team-Ordner ist ein Space ohne Besitzer; Administratoren legen ihn an und geben Benutzern oder Gruppen
/// eine Rolle bis Verwalter. Die wirksame Rolle eines Benutzers ist die höchste aus direkter Mitgliedschaft und seinen Gruppen.
/// </summary>
public sealed partial class AdminService
{
    private const int MaxName = 64;
    private static readonly SpaceRole[] TeamRoles = [SpaceRole.Previewer, SpaceRole.Viewer, SpaceRole.Editor, SpaceRole.Manager];

    public async Task<ServiceResult<GroupsResponse>> ListGroupsAsync(Principal caller, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<GroupsResponse>();
        }

        var result = new List<GroupInfo>();
        foreach (var group in await groups.ListGroupsAsync(ct))
        {
            result.Add(await DescribeAsync(group, ct));
        }

        return ServiceResult.Ok(new GroupsResponse(result));
    }

    public async Task<ServiceResult<GroupInfo>> CreateGroupAsync(Principal caller, NameRequest request, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<GroupInfo>();
        }

        if (ValidName(request.Name) is not { } name)
        {
            return Fail<GroupInfo>(ErrorCode.InvalidRequest, "Name ungültig.");
        }

        var group = new GroupRecord("g_" + Guid.NewGuid().ToString("N"), name);
        return await groups.AddGroupAsync(group, ct)
            ? ServiceResult.Ok(new GroupInfo(group.Id, group.Name, []))
            : Fail<GroupInfo>(ErrorCode.AlreadyExists, "Eine Gruppe mit diesem Namen gibt es schon.");
    }

    public async Task<ServiceError?> DeleteGroupAsync(Principal caller, string groupId, CancellationToken ct) =>
        !caller.IsAdmin ? new ServiceError(ErrorCode.Forbidden, "Nur für Administratoren.")
        : await groups.DeleteGroupAsync(groupId, ct) ? null
        : new ServiceError(ErrorCode.NotFound, "Gruppe unbekannt.");

    public async Task<ServiceResult<GroupInfo>> SetGroupMemberAsync(
        Principal caller, string groupId, string userId, bool member, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<GroupInfo>();
        }

        if (await groups.FindGroupAsync(groupId, ct) is not { } group)
        {
            return Fail<GroupInfo>(ErrorCode.NotFound, "Gruppe unbekannt.");
        }

        if (await store.FindUserByIdAsync(userId, ct) is null)
        {
            return Fail<GroupInfo>(ErrorCode.NotFound, "Benutzer unbekannt.");
        }

        await groups.SetGroupMemberAsync(groupId, userId, member, ct);
        return ServiceResult.Ok(await DescribeAsync(group, ct));
    }

    public async Task<ServiceResult<TeamFoldersResponse>> ListTeamFoldersAsync(Principal caller, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<TeamFoldersResponse>();
        }

        var result = new List<TeamFolderInfo>();
        foreach (var (id, name) in await store.ListTeamSpacesAsync(ct))
        {
            result.Add(await DescribeTeamAsync(id, name, ct));
        }

        return ServiceResult.Ok(new TeamFoldersResponse(result));
    }

    public async Task<ServiceResult<TeamFolderInfo>> CreateTeamFolderAsync(Principal caller, NameRequest request, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<TeamFolderInfo>();
        }

        if (ValidName(request.Name) is not { } name)
        {
            return Fail<TeamFolderInfo>(ErrorCode.InvalidRequest, "Name ungültig.");
        }

        if ((await store.ListTeamSpacesAsync(ct)).Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail<TeamFolderInfo>(ErrorCode.AlreadyExists, "Einen Team-Ordner mit diesem Namen gibt es schon.");
        }

        var spaceId = "sp_" + Guid.NewGuid().ToString("N");
        await store.CreateTeamSpaceAsync(spaceId, name, ct);
        return ServiceResult.Ok(new TeamFolderInfo(spaceId, name, 0, []));
    }

    public async Task<ServiceResult<TeamFolderInfo>> RenameTeamFolderAsync(Principal caller, string spaceId, NameRequest request, CancellationToken ct)
    {
        if (await TeamAsync(caller, spaceId, ct) is { IsSuccess: false } denied)
        {
            return ServiceResult.Fail<TeamFolderInfo>(denied.Error!);
        }

        if (ValidName(request.Name) is not { } name)
        {
            return Fail<TeamFolderInfo>(ErrorCode.InvalidRequest, "Name ungültig.");
        }

        if ((await store.ListTeamSpacesAsync(ct)).Any(t => t.Id != spaceId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail<TeamFolderInfo>(ErrorCode.AlreadyExists, "Einen Team-Ordner mit diesem Namen gibt es schon.");
        }

        await store.RenameSpaceAsync(spaceId, name, ct);
        return ServiceResult.Ok(await DescribeTeamAsync(spaceId, name, ct));
    }

    /// <summary>Benutzer oder Gruppe mit Rolle hinzufügen oder die Rolle ändern; neue Mitglieder erfahren es per Benachrichtigung.</summary>
    public async Task<ServiceResult<TeamFolderInfo>> SetTeamMemberAsync(
        Principal caller, string spaceId, TeamMemberRequest request, CancellationToken ct)
    {
        var team = await TeamAsync(caller, spaceId, ct);
        if (!team.IsSuccess)
        {
            return ServiceResult.Fail<TeamFolderInfo>(team.Error!);
        }

        if (!Enum.TryParse<SpaceRole>(request.Role, ignoreCase: true, out var role) || !TeamRoles.Contains(role))
        {
            return Fail<TeamFolderInfo>(ErrorCode.InvalidRequest, "Unbekannte Rolle.");
        }

        IReadOnlyList<UserRecord> newcomers;
        switch (request.Kind)
        {
            case SpaceMember.User when await store.FindUserByIdAsync(request.Id ?? string.Empty, ct) is { } user:
                newcomers = await store.GetRoleAsync(user.Id, spaceId, ct) is null ? [user] : [];
                await store.SetMemberAsync(spaceId, user.Id, role, ct);
                break;
            case SpaceMember.Group when await groups.FindGroupAsync(request.Id ?? string.Empty, ct) is { } group:
                newcomers = [.. await NewcomersAsync(await groups.ListGroupMembersAsync(group.Id, ct), spaceId, ct)];
                await store.SetGroupRoleAsync(spaceId, group.Id, role, ct);
                break;
            default:
                return Fail<TeamFolderInfo>(ErrorCode.NotFound, "Benutzer oder Gruppe unbekannt.");
        }

        foreach (var newcomer in newcomers)
        {
            await notes.SharedAsync(caller, spaceId, newcomer.Name, ct);
        }

        return ServiceResult.Ok(await DescribeTeamAsync(spaceId, team.Value!, ct));
    }

    public async Task<ServiceResult<TeamFolderInfo>> RemoveTeamMemberAsync(
        Principal caller, string spaceId, string kind, string memberId, CancellationToken ct)
    {
        var team = await TeamAsync(caller, spaceId, ct);
        if (!team.IsSuccess)
        {
            return ServiceResult.Fail<TeamFolderInfo>(team.Error!);
        }

        var removed = kind switch
        {
            SpaceMember.User => await store.RemoveMemberAsync(spaceId, memberId, ct),
            SpaceMember.Group => await store.RemoveGroupAsync(spaceId, memberId, ct),
            _ => false,
        };
        return removed
            ? ServiceResult.Ok(await DescribeTeamAsync(spaceId, team.Value!, ct))
            : Fail<TeamFolderInfo>(ErrorCode.NotFound, "Kein Mitglied dieses Team-Ordners.");
    }

    /// <summary>Prüft Admin-Recht und dass es ein Team-Ordner ist (eigene Spaces der Benutzer verwaltet die Konsole nicht); liefert den Namen.</summary>
    private async Task<ServiceResult<string>> TeamAsync(Principal caller, string spaceId, CancellationToken ct)
    {
        if (!caller.IsAdmin)
        {
            return Forbidden<string>();
        }

        return (await store.ListTeamSpacesAsync(ct)).FirstOrDefault(t => t.Id == spaceId) is { Id: not null } team
            ? ServiceResult.Ok(team.Name)
            : Fail<string>(ErrorCode.NotFound, "Team-Ordner unbekannt.");
    }

    private async Task<IEnumerable<UserRecord>> NewcomersAsync(IReadOnlyList<UserRecord> users, string spaceId, CancellationToken ct)
    {
        var result = new List<UserRecord>();
        foreach (var user in users)
        {
            if (await store.GetRoleAsync(user.Id, spaceId, ct) is null)
            {
                result.Add(user);
            }
        }

        return result;
    }

    private async Task<GroupInfo> DescribeAsync(GroupRecord group, CancellationToken ct) => new(
        group.Id, group.Name, [.. (await groups.ListGroupMembersAsync(group.Id, ct)).Select(u => new GroupMemberInfo(u.Id, u.Name))]);

    private async Task<TeamFolderInfo> DescribeTeamAsync(string spaceId, string name, CancellationToken ct) => new(
        spaceId, name, await spaces.GetUsedBytesAsync(spaceId, ct),
        [.. (await store.ListSpaceMembersAsync(spaceId, ct)).Select(m => new TeamMemberInfo(m.Kind, m.Id, m.Name, m.Role.ToString().ToLowerInvariant()))]);

    private static string? ValidName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        return name.Length is 0 or > MaxName || name.Any(char.IsControl) ? null : name;
    }
}
