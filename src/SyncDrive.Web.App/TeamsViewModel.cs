using System.Globalization;
using SyncDrive.Contracts;

namespace SyncDrive.Web.App;

/// <summary>Kandidat für einen Team-Ordner: ein Benutzer oder eine Gruppe.</summary>
public sealed record MemberCandidate(string Kind, string Id, string Name);

/// <summary>
/// Verwaltung, Reiter „Gruppen“ und „Team-Ordner“ (M7): Gruppen anlegen, löschen und ihre Mitglieder an- oder abhaken; Team-Ordner anlegen,
/// umbenennen und Benutzern oder Gruppen eine Rolle geben. Die wirksame Rolle eines Benutzers ist die höchste (der Server rechnet sie aus).
/// </summary>
public sealed class TeamsViewModel(IDriveApi api, WebTexts texts) : ViewModel(texts)
{
    public const string DefaultRole = "editor";

    public static IReadOnlyList<string> Roles { get; } = ["previewer", "viewer", "editor", "manager"];

    public ListState State { get; private set; } = ListState.Loading;

    public IReadOnlyList<GroupInfo> Groups { get; private set; } = [];

    public IReadOnlyList<TeamFolderInfo> TeamFolders { get; private set; } = [];

    public IReadOnlyList<AdminUserInfo> Users { get; private set; } = [];

    public GroupInfo? SelectedGroup { get; private set; }

    public TeamFolderInfo? SelectedTeam { get; private set; }

    public async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync(async () =>
        {
            Groups = await api.ListGroupsAsync(ct);
            TeamFolders = await api.ListTeamFoldersAsync(ct);
            Users = await api.ListUsersAsync(ct);
        }, Explain);
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == SelectedGroup?.Id);
        SelectedTeam = TeamFolders.FirstOrDefault(t => t.SpaceId == SelectedTeam?.SpaceId);
        State = ListState.Ready;
        OnChanged();
    }

    public void Select(GroupInfo group)
    {
        SelectedGroup = group;
        OnChanged();
    }

    public void Select(TeamFolderInfo team)
    {
        SelectedTeam = team;
        OnChanged();
    }

    public static bool IsMember(GroupInfo group, AdminUserInfo user) => group.Members.Any(m => m.UserId == user.Id);

    /// <summary>„2 Benutzer, 1 Gruppe“.</summary>
    public string MembersSummary(TeamFolderInfo team) => string.Format(
        CultureInfo.CurrentCulture, Texts.MembersSummary, team.Members.Count(m => m.Kind == "user"), team.Members.Count(m => m.Kind == "group"));

    /// <summary>Wer sich noch hinzufügen lässt: Gruppen zuerst, dann Benutzer, jeweils ohne die schon aufgenommenen.</summary>
    public IReadOnlyList<MemberCandidate> Candidates(TeamFolderInfo team) =>
    [
        .. Groups.Where(g => !team.Members.Any(m => m.Kind == "group" && m.Id == g.Id)).Select(g => new MemberCandidate("group", g.Id, g.Name)),
        .. Users.Where(u => !team.Members.Any(m => m.Kind == "user" && m.Id == u.Id)).Select(u => new MemberCandidate("user", u.Id, u.Name)),
    ];

    public async Task<bool> CreateGroupAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail(Texts.InvalidName);
        }

        GroupInfo? created = null;
        var ok = await RunAsync(async () => created = await api.CreateGroupAsync(name.Trim(), ct), Explain);
        await LoadAsync(ct);
        if (ok)
        {
            SelectedGroup = Groups.FirstOrDefault(g => g.Id == created!.Id);
            OnChanged();
        }

        return ok;
    }

    public async Task<bool> DeleteGroupAsync(GroupInfo group, CancellationToken ct)
    {
        var ok = await RunAsync(() => api.DeleteGroupAsync(group.Id, ct), Explain);
        await LoadAsync(ct);
        return ok;
    }

    public async Task ToggleGroupMemberAsync(GroupInfo group, AdminUserInfo user, CancellationToken ct)
    {
        await RunAsync(() => api.SetGroupMemberAsync(group.Id, user.Id, !IsMember(group, user), ct), Explain);
        await LoadAsync(ct);
    }

    public async Task<bool> CreateTeamFolderAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail(Texts.InvalidName);
        }

        TeamFolderInfo? created = null;
        var ok = await RunAsync(async () => created = await api.CreateTeamFolderAsync(name.Trim(), ct), Explain);
        await LoadAsync(ct);
        if (ok)
        {
            SelectedTeam = TeamFolders.FirstOrDefault(t => t.SpaceId == created!.SpaceId);
            OnChanged();
        }

        return ok;
    }

    public async Task<bool> RenameTeamFolderAsync(TeamFolderInfo team, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail(Texts.InvalidName);
        }

        var ok = await RunAsync(() => api.RenameTeamFolderAsync(team.SpaceId, name.Trim(), ct), Explain);
        await LoadAsync(ct);
        return ok;
    }

    public async Task SetMemberAsync(TeamFolderInfo team, string kind, string id, string role, CancellationToken ct)
    {
        await RunAsync(() => api.SetTeamMemberAsync(team.SpaceId, kind, id, role, ct), Explain);
        await LoadAsync(ct);
    }

    public async Task RemoveMemberAsync(TeamFolderInfo team, TeamMemberInfo member, CancellationToken ct)
    {
        await RunAsync(() => api.RemoveTeamMemberAsync(team.SpaceId, member.Kind, member.Id, ct), Explain);
        await LoadAsync(ct);
    }

    private string? Explain(DriveException e) => e.Code switch
    {
        ErrorCode.AlreadyExists => Texts.NameTakenAdmin,
        ErrorCode.Forbidden => Texts.NoPermission,
        ErrorCode.InvalidRequest => string.Format(CultureInfo.CurrentCulture, Texts.GenericError, e.Message),
        _ => null,
    };
}
