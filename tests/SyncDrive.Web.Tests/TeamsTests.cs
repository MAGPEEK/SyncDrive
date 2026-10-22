using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>M7 Verwaltung, Gruppen und Team-Ordner im Browser gegen den echten Host.</summary>
public sealed class TeamsTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task A_group_gets_a_team_folder_and_its_members_see_it_in_the_browser()
    {
        var adminPage = await WebPage.OpenAsync(factory, HostFactory.AdminName, HostFactory.AdminPassword);
        var lena = await WebPage.OpenAsync(factory);
        var teams = new TeamsViewModel(adminPage.Api, WebTexts.English);
        await teams.LoadAsync(default);
        var groupName = Unique("Redaktion");
        var teamName = Unique("Zeitung");

        await teams.CreateGroupAsync(groupName, default);
        var group = teams.SelectedGroup!;
        await teams.ToggleGroupMemberAsync(group, teams.Users.Single(u => u.Name == lena.User), default);
        await teams.CreateTeamFolderAsync(teamName, default);
        var team = teams.SelectedTeam!;
        var before = teams.Candidates(team).Select(c => (c.Kind, c.Name)).ToList();
        await teams.SetMemberAsync(team, "group", group.Id, "editor", default);
        var withGroup = teams.SelectedTeam!;
        await lena.Browser.ReloadSpacesAsync(default);
        var seen = lena.Browser.Spaces.Single(s => s.Name == teamName);
        var twice = await teams.CreateGroupAsync(groupName.ToUpperInvariant(), default);
        var twiceMessage = teams.Message;
        await teams.RenameTeamFolderAsync(withGroup, teamName + " 2026", default);
        await teams.RemoveMemberAsync(teams.SelectedTeam!, teams.SelectedTeam!.Members.Single(), default);
        await lena.Browser.ReloadSpacesAsync(default);

        Assert.Contains(("group", groupName), before);
        Assert.Contains(("user", lena.User), before);
        Assert.True(TeamsViewModel.IsMember(teams.Groups.Single(g => g.Id == group.Id), teams.Users.Single(u => u.Name == lena.User)));
        Assert.Equal(("0 user(s), 1 group(s)", "editor"), (teams.MembersSummary(withGroup), seen.Role));
        Assert.DoesNotContain(teams.Candidates(withGroup), c => c.Id == group.Id);
        Assert.Equal((false, WebTexts.English.NameTakenAdmin), (twice, twiceMessage));
        Assert.Equal(teamName + " 2026", teams.SelectedTeam!.Name);
        Assert.Empty(teams.SelectedTeam.Members);
        Assert.DoesNotContain(lena.Browser.Spaces, s => s.Id == team.SpaceId);
    }
}
