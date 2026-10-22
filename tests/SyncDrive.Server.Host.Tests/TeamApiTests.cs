using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>M7 Gruppen und Team-Ordner über HTTP: Rollen direkt und über Gruppen, die höchste gilt; nur Administratoren verwalten.</summary>
public class TeamApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<(ApiClient Api, string Id, string Name)> UserAsync(ApiClient admin, string prefix)
    {
        var name = Unique(prefix);
        using var created = await admin.Http.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(name, Password));
        var info = (await created.Content.ReadFromJsonAsync<AdminUserInfo>())!;
        return (await factory.LoginAsync(name, Password), info.Id, name);
    }

    private static async Task<TeamFolderInfo> TeamAsync(ApiClient admin, string name)
    {
        using var created = await admin.Http.PostAsJsonAsync("/api/v1/admin/team-folders", new NameRequest(name));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<TeamFolderInfo>())!;
    }

    private static async Task<HttpResponseMessage> MemberAsync(ApiClient admin, string spaceId, string kind, string id, string role) =>
        await admin.Http.PutAsJsonAsync($"/api/v1/admin/team-folders/{spaceId}/members", new TeamMemberRequest(kind, id, role));

    private static async Task<SpaceSummary?> SeesAsync(ApiClient api, string spaceId) =>
        (await api.Http.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!.Spaces.FirstOrDefault(s => s.Id == spaceId);

    private static async Task<HttpResponseMessage> PutAsync(ApiClient api, string space, string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        using (var body = new ByteArrayContent(bytes))
        {
            (await api.Http.PutAsync($"/api/v1/blocks/{hash}", body)).EnsureSuccessStatusCode();
        }

        return await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, path, 0, Blocks: [hash], Size: bytes.Length, MTime: DateTimeOffset.UtcNow, FileHash: hash));
    }

    [Fact]
    public async Task A_team_folder_appears_for_its_members_with_their_role_and_they_hear_about_it()
    {
        var admin = await factory.LoginAsync();
        var (anna, annaId, _) = await UserAsync(admin, "anna");
        var name = Unique("Marketing");
        var team = await TeamAsync(admin, name);

        using var add = await MemberAsync(admin, team.SpaceId, "user", annaId, "editor");
        var seen = await SeesAsync(anna, team.SpaceId);
        using var upload = await PutAsync(anna, team.SpaceId, "plan.txt");
        var note = Assert.Single((await anna.Http.GetFromJsonAsync<NotificationsResponse>("/api/v1/notifications"))!.Notifications);
        var listed = (await admin.Http.GetFromJsonAsync<TeamFoldersResponse>("/api/v1/admin/team-folders"))!.TeamFolders.Single(t => t.SpaceId == team.SpaceId);

        Assert.Equal((name, "editor", (string?)null), (seen!.Name, seen.Role, seen.SharedBy));
        Assert.True(upload.IsSuccessStatusCode);
        Assert.Equal(("shared", team.SpaceId), (note.Type, note.SpaceId));
        Assert.Equal((8L, "user", "editor"), (listed.UsedBytes, listed.Members.Single().Kind, listed.Members.Single().Role));
    }

    [Fact]
    public async Task Group_members_get_the_group_role_and_the_highest_role_wins()
    {
        var admin = await factory.LoginAsync();
        var (ben, benId, _) = await UserAsync(admin, "ben");
        var team = await TeamAsync(admin, Unique("Vertrieb"));
        using var groupResponse = await admin.Http.PostAsJsonAsync("/api/v1/admin/groups", new NameRequest(Unique("Lesende")));
        var group = (await groupResponse.Content.ReadFromJsonAsync<GroupInfo>())!;

        (await admin.Http.PutAsync($"/api/v1/admin/groups/{group.Id}/members/{benId}", null)).EnsureSuccessStatusCode();
        (await MemberAsync(admin, team.SpaceId, "group", group.Id, "viewer")).EnsureSuccessStatusCode();
        var viaGroup = await SeesAsync(ben, team.SpaceId);
        using var viewerWrites = await PutAsync(ben, team.SpaceId, "a.txt");
        (await MemberAsync(admin, team.SpaceId, "user", benId, "editor")).EnsureSuccessStatusCode();
        var both = await SeesAsync(ben, team.SpaceId);
        (await admin.Http.DeleteAsync($"/api/v1/admin/team-folders/{team.SpaceId}/members/user/{benId}")).EnsureSuccessStatusCode();
        (await admin.Http.DeleteAsync($"/api/v1/admin/groups/{group.Id}/members/{benId}")).EnsureSuccessStatusCode();
        var none = await SeesAsync(ben, team.SpaceId);
        using var gone = await ben.Http.GetAsync($"/api/v1/spaces/{team.SpaceId}/snapshot");

        Assert.Equal("viewer", viaGroup!.Role);
        Assert.Equal(HttpStatusCode.Forbidden, viewerWrites.StatusCode);
        Assert.Equal("editor", both!.Role);
        Assert.Null(none);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_group_removes_its_rights()
    {
        var admin = await factory.LoginAsync();
        var (ben, benId, _) = await UserAsync(admin, "ben");
        var team = await TeamAsync(admin, Unique("Technik"));
        var group = (await (await admin.Http.PostAsJsonAsync("/api/v1/admin/groups", new NameRequest(Unique("Technik")))).Content.ReadFromJsonAsync<GroupInfo>())!;
        (await admin.Http.PutAsync($"/api/v1/admin/groups/{group.Id}/members/{benId}", null)).EnsureSuccessStatusCode();
        (await MemberAsync(admin, team.SpaceId, "group", group.Id, "manager")).EnsureSuccessStatusCode();
        var before = await SeesAsync(ben, team.SpaceId);

        using var deleted = await admin.Http.DeleteAsync($"/api/v1/admin/groups/{group.Id}");
        var groups = (await admin.Http.GetFromJsonAsync<GroupsResponse>("/api/v1/admin/groups"))!.Groups;

        Assert.Equal("manager", before!.Role);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await SeesAsync(ben, team.SpaceId));
        Assert.DoesNotContain(groups, g => g.Id == group.Id);
    }

    [Fact]
    public async Task Team_folders_follow_the_rules_for_names_roles_and_admins()
    {
        var admin = await factory.LoginAsync();
        var (anna, annaId, _) = await UserAsync(admin, "anna");
        var name = Unique("Einkauf");
        var team = await TeamAsync(admin, name);
        await TeamAsync(admin, name + "-2");

        using var twice = await admin.Http.PostAsJsonAsync("/api/v1/admin/team-folders", new NameRequest(name.ToUpperInvariant()));
        using var renameTaken = await admin.Http.PatchAsJsonAsync($"/api/v1/admin/team-folders/{team.SpaceId}", new NameRequest(name + "-2"));
        using var renamed = await admin.Http.PatchAsJsonAsync($"/api/v1/admin/team-folders/{team.SpaceId}", new NameRequest(name + " neu"));
        using var owner = await MemberAsync(admin, team.SpaceId, "user", annaId, "owner");
        using var unknown = await MemberAsync(admin, team.SpaceId, "group", "g_gibt-es-nicht", "viewer");
        using var personal = await MemberAsync(admin, await anna.FirstSpaceIdAsync(), "user", annaId, "viewer");
        using var asAnna = await anna.Http.GetAsync("/api/v1/admin/team-folders");
        using var annaCreates = await anna.Http.PostAsJsonAsync("/api/v1/admin/groups", new NameRequest(Unique("x")));
        using var emptyName = await admin.Http.PostAsJsonAsync("/api/v1/admin/groups", new NameRequest("  "));

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (twice.StatusCode, renameTaken.StatusCode));
        Assert.Equal(name + " neu", (await renamed.Content.ReadFromJsonAsync<TeamFolderInfo>())!.Name);
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (owner.StatusCode, unknown.StatusCode, personal.StatusCode));
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (asAnna.StatusCode, annaCreates.StatusCode));
        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);
    }
}
