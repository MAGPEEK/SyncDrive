using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>M6b über HTTP: Einladungen auf Ordner (als eigener Space), öffentliche Links mit Passwort und Ablauf, Dateianfragen.</summary>
public class SharingApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static string HashOf(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    private static string Q(string value) => Uri.EscapeDataString(value);

    private async Task<(ApiClient Api, string Name, string Space)> UserAsync(string prefix)
    {
        var name = await factory.CreateUserAsync(Unique(prefix));
        var api = await factory.LoginAsync(name, Password);
        return (api, name, await api.FirstSpaceIdAsync());
    }

    private static async Task<HttpResponseMessage> PutAsync(ApiClient api, string space, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using (var body = new ByteArrayContent(bytes))
        {
            (await api.Http.PutAsync($"/api/v1/blocks/{HashOf(bytes)}", body)).EnsureSuccessStatusCode();
        }

        return await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, path, 0, Blocks: [HashOf(bytes)], Size: bytes.Length, MTime: T0, FileHash: HashOf(bytes)));
    }

    private static async Task MkdirAsync(ApiClient api, string space, string path) =>
        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, path, 0, MTime: T0)))
        .EnsureSuccessStatusCode();

    private static async Task<List<string>> ListAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<SnapshotEntry>(line, System.Text.Json.JsonSerializerOptions.Web)!.Path)];
    }

    /// <summary>Anna mit „Projekte/plan.txt“ und „geheim.txt“.</summary>
    private async Task<(ApiClient Anna, string AnnaName, string Space)> AnnaWithProjectAsync()
    {
        var (anna, name, space) = await UserAsync("anna");
        await MkdirAsync(anna, space, "Projekte");
        (await PutAsync(anna, space, "Projekte/plan.txt", "Plan")).EnsureSuccessStatusCode();
        (await PutAsync(anna, space, "geheim.txt", "geheim")).EnsureSuccessStatusCode();
        return (anna, name, space);
    }

    private static Task<HttpResponseMessage> InviteAsync(ApiClient api, string space, string path, string user, string role) =>
        api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/sharing/invitees?path={Q(path)}", new InviteRequest(user, role));

    private static async Task<LinkInfo> LinkAsync(ApiClient api, string space, string path, string kind, LinkRequest? request = null)
    {
        using var response = await api.Http.PutAsJsonAsync($"/api/v1/spaces/{space}/sharing/link?path={Q(path)}&kind={kind}", request ?? new LinkRequest(null));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LinkInfo>())!;
    }

    private HttpClient Anonymous() => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    [Fact]
    public async Task An_invited_user_sees_the_shared_folder_as_a_space_of_its_own_and_works_in_it()
    {
        var (anna, annaName, space) = await AnnaWithProjectAsync();
        var (ben, benName, _) = await UserAsync("ben");

        using var invite = await InviteAsync(anna, space, "Projekte", benName, "editor");
        var invitee = (await invite.Content.ReadFromJsonAsync<InviteeInfo>())!;
        var shared = Assert.Single((await ben.Http.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!.Spaces, s => s.SharedBy is not null);
        var listing = await ListAsync(ben.Http, $"/api/v1/spaces/{shared.Id}/snapshot");
        using var put = await PutAsync(ben, shared.Id, "neu.txt", "von Ben");
        var annaSees = await ListAsync(anna.Http, $"/api/v1/spaces/{space}/snapshot?path=Projekte");
        var note = Assert.Single((await ben.Http.GetFromJsonAsync<NotificationsResponse>("/api/v1/notifications"))!.Notifications);
        var sharing = (await anna.Http.GetFromJsonAsync<SharingResponse>($"/api/v1/spaces/{space}/sharing?path=Projekte"))!;

        Assert.Equal((benName, "editor"), (invitee.UserName, invitee.Role));
        Assert.Equal((invitee.ShareId, "Projekte", "editor", annaName), (shared.Id, shared.Name, shared.Role, shared.SharedBy));
        Assert.StartsWith("sh_", shared.Id, StringComparison.Ordinal);
        Assert.Equal(["plan.txt"], listing);
        Assert.True(put.IsSuccessStatusCode);
        Assert.Equal(["Projekte/neu.txt", "Projekte/plan.txt"], annaSees);
        Assert.Equal(("shared", shared.Id, annaName), (note.Type, note.SpaceId, note.Actor));
        Assert.Equal([invitee], sharing.Invitees);
    }

    [Fact]
    public async Task Invitations_follow_the_rules_for_roles_people_and_kinds()
    {
        var (anna, annaName, space) = await AnnaWithProjectAsync();
        var (ben, benName, _) = await UserAsync("ben");

        using var viewer = await InviteAsync(anna, space, "Projekte", benName, "viewer");
        var shareId = (await viewer.Content.ReadFromJsonAsync<InviteeInfo>())!.ShareId;
        using var benWrites = await PutAsync(ben, shareId, "x.txt", "x");
        using var self = await InviteAsync(anna, space, "Projekte", annaName, "viewer");
        using var unknown = await InviteAsync(anna, space, "Projekte", "niemand-" + Guid.NewGuid().ToString("N"), "viewer");
        using var file = await InviteAsync(anna, space, "geheim.txt", benName, "viewer");
        using var manager = await InviteAsync(anna, space, "Projekte", benName, "manager");
        using var reshare = await ben.Http.GetAsync($"/api/v1/spaces/{shareId}/sharing?path=plan.txt");
        using var benInAnnasSpace = await ben.Http.GetAsync($"/api/v1/spaces/{space}/snapshot");
        using var escape = await ben.Http.GetAsync($"/api/v1/spaces/{shareId}/node?path={Q("../geheim.txt")}");

        Assert.Equal(HttpStatusCode.Forbidden, benWrites.StatusCode);
        Assert.Equal(
            (HttpStatusCode.Conflict, HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest),
            (self.StatusCode, unknown.StatusCode, file.StatusCode, manager.StatusCode));
        Assert.Equal(HttpStatusCode.Forbidden, reshare.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, benInAnnasSpace.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);
    }

    [Fact]
    public async Task Removing_an_invitation_or_leaving_takes_the_folder_away()
    {
        var (anna, _, space) = await AnnaWithProjectAsync();
        await MkdirAsync(anna, space, "Fotos");
        var (ben, benName, _) = await UserAsync("ben");
        var first = (await (await InviteAsync(anna, space, "Projekte", benName, "viewer")).Content.ReadFromJsonAsync<InviteeInfo>())!;
        var second = (await (await InviteAsync(anna, space, "Fotos", benName, "viewer")).Content.ReadFromJsonAsync<InviteeInfo>())!;

        using var removed = await anna.Http.DeleteAsync($"/api/v1/sharing/invitees/{first.ShareId}");
        using var left = await ben.Http.DeleteAsync($"/api/v1/sharing/invitees/{second.ShareId}");
        using var gone = await ben.Http.GetAsync($"/api/v1/spaces/{first.ShareId}/snapshot");
        var spaces = (await ben.Http.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!.Spaces;

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (removed.StatusCode, left.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.DoesNotContain(spaces, s => s.SharedBy is not null);
    }

    [Fact]
    public async Task A_public_folder_link_lists_downloads_and_zips_without_signing_in()
    {
        var (anna, annaName, space) = await AnnaWithProjectAsync();
        var link = await LinkAsync(anna, space, "Projekte", LinkKinds.Share);
        var web = Anonymous();
        var at = $"/api/v1/public/{link.Token}";

        var info = (await web.GetFromJsonAsync<PublicLinkInfo>(at))!;
        var listing = (await web.GetFromJsonAsync<FindResponse>($"{at}/list"))!.Entries.Select(e => e.Path);
        var content = await web.GetStringAsync($"{at}/download?path=plan.txt");
        using var zipResponse = await web.GetAsync($"{at}/zip");
        using var zip = new ZipArchive(await zipResponse.Content.ReadAsStreamAsync());
        using var outside = await web.GetAsync($"{at}/download?path={Q("../geheim.txt")}");
        using var upload = await web.PutAsync($"{at}/files?name=x.txt", new StringContent("x"));
        using var unknown = await web.GetAsync("/api/v1/public/gibt-es-nicht");

        Assert.Equal((LinkKinds.Share, "Projekte", NodeKinds.Directory, annaName, false), (info.Kind, info.Name, info.ItemKind, info.Owner, info.PasswordRequired));
        Assert.Equal(["plan.txt"], listing);
        Assert.Equal("Plan", content);
        Assert.Equal("Projekte.zip", zipResponse.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(["plan.txt"], zip.Entries.Select(e => e.FullName));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_password_unlocks_the_link_for_this_browser_until_the_password_changes()
    {
        var (anna, _, space) = await AnnaWithProjectAsync();
        var link = await LinkAsync(anna, space, "Projekte", LinkKinds.Share, new LinkRequest(null, "sesam123"));
        var web = Anonymous();
        var at = $"/api/v1/public/{link.Token}";

        var locked = (await web.GetFromJsonAsync<PublicLinkInfo>(at))!;
        using var listLocked = await web.GetAsync($"{at}/list");
        using var wrong = await web.PostAsJsonAsync($"{at}/password", new LinkPasswordRequest("falsch"));
        using var right = await web.PostAsJsonAsync($"{at}/password", new LinkPasswordRequest("sesam123"));
        using var listUnlocked = await web.GetAsync($"{at}/list");
        var changed = await LinkAsync(anna, space, "Projekte", LinkKinds.Share, new LinkRequest(null, "neues-geheimnis"));
        using var afterChange = await web.GetAsync($"{at}/list");
        var removedPassword = await LinkAsync(anna, space, "Projekte", LinkKinds.Share, new LinkRequest(null, RemovePassword: true));
        using var open = await Anonymous().GetAsync($"{at}/list");

        Assert.True(link.HasPassword);
        Assert.True(locked.PasswordRequired);
        Assert.Equal(
            (HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.OK, HttpStatusCode.OK),
            (listLocked.StatusCode, wrong.StatusCode, right.StatusCode, listUnlocked.StatusCode));
        Assert.Equal(link.Token, changed.Token); // verschickte Links bleiben gültig
        Assert.Equal(HttpStatusCode.Unauthorized, afterChange.StatusCode);
        Assert.False(removedPassword.HasPassword);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    [Fact]
    public async Task Links_cannot_expire_in_the_past_and_a_removed_link_is_gone()
    {
        var (anna, _, space) = await AnnaWithProjectAsync();
        var link = await LinkAsync(anna, space, "geheim.txt", LinkKinds.Share, new LinkRequest(DateTimeOffset.UtcNow.AddDays(7)));
        var web = Anonymous();

        using var past = await anna.Http.PutAsJsonAsync(
            $"/api/v1/spaces/{space}/sharing/link?path=geheim.txt&kind=share", new LinkRequest(DateTimeOffset.UtcNow.AddDays(-1)));
        var file = await web.GetStringAsync($"/api/v1/public/{link.Token}/download");
        using var list = await web.GetAsync($"/api/v1/public/{link.Token}/list");
        using var removed = await anna.Http.DeleteAsync($"/api/v1/sharing/links/{link.Id}");
        using var after = await web.GetAsync($"/api/v1/public/{link.Token}");
        using var shortPassword = await anna.Http.PutAsJsonAsync(
            $"/api/v1/spaces/{space}/sharing/link?path=geheim.txt&kind=share", new LinkRequest(null, "123"));

        Assert.NotNull(link.ExpiresAt);
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Equal("geheim", file);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode); // ein Datei-Link hat nichts aufzulisten
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (removed.StatusCode, after.StatusCode));
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
    }

    [Fact]
    public async Task A_file_request_takes_uploads_without_overwriting_and_shows_nothing()
    {
        var (anna, _, space) = await AnnaWithProjectAsync();
        var request = await LinkAsync(anna, space, "Projekte", LinkKinds.Request);
        var web = Anonymous();
        var at = $"/api/v1/public/{request.Token}";

        using var first = await web.PutAsync($"{at}/files?name={Q("plan.txt")}", new StringContent("von außen"));
        using var second = await web.PutAsync($"{at}/files?name={Q("plan.txt")}", new StringContent("noch einmal"));
        using var badName = await web.PutAsync($"{at}/files?name={Q("../böse.txt")}", new StringContent("x"));
        using var list = await web.GetAsync($"{at}/list");
        using var download = await web.GetAsync($"{at}/download?path=plan.txt");
        var info = (await web.GetFromJsonAsync<PublicLinkInfo>(at))!;
        var annaSees = await ListAsync(anna.Http, $"/api/v1/spaces/{space}/snapshot?path=Projekte");
        using var onFile = await anna.Http.PutAsJsonAsync($"/api/v1/spaces/{space}/sharing/link?path=geheim.txt&kind=request", new LinkRequest(null));

        Assert.Equal(
            ("plan (2).txt", "plan (3).txt"),
            ((await first.Content.ReadFromJsonAsync<PublicUploadResponse>())!.Name, (await second.Content.ReadFromJsonAsync<PublicUploadResponse>())!.Name));
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (list.StatusCode, download.StatusCode));
        Assert.Equal(LinkKinds.Request, info.Kind);
        Assert.Equal(["Projekte/plan (2).txt", "Projekte/plan (3).txt", "Projekte/plan.txt"], annaSees);
        Assert.Equal(HttpStatusCode.BadRequest, onFile.StatusCode);
    }

    [Fact]
    public async Task Shared_with_others_lists_what_the_user_shared_until_it_is_deleted()
    {
        var (anna, _, space) = await AnnaWithProjectAsync();
        var (_, benName, _) = await UserAsync("ben");
        await InviteAsync(anna, space, "Projekte", benName, "viewer");
        var link = await LinkAsync(anna, space, "geheim.txt", LinkKinds.Share);

        var items = (await anna.Http.GetFromJsonAsync<SharedItemsResponse>("/api/v1/sharing/by-me"))!.Items;
        var node = (await anna.Http.GetFromJsonAsync<NodeDetail>($"/api/v1/spaces/{space}/node?path=geheim.txt"))!;
        (await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Delete, "geheim.txt", node.Node.Revision)))
            .EnsureSuccessStatusCode();
        var after = (await anna.Http.GetFromJsonAsync<SharedItemsResponse>("/api/v1/sharing/by-me"))!.Items;
        using var deadLink = await Anonymous().GetAsync($"/api/v1/public/{link.Token}");

        Assert.Equal(["geheim.txt", "Projekte"], items.Select(i => i.Path)); // ohne Rücksicht auf Groß- und Kleinschreibung
        Assert.Equal([benName], items[1].Invitees.Select(i => i.UserName));
        Assert.Equal(link.Id, items[0].Link!.Id);
        Assert.Equal(["Projekte"], after.Select(i => i.Path));
        Assert.Equal(HttpStatusCode.NotFound, deadLink.StatusCode);
    }

    [Fact]
    public async Task The_invitee_is_notified_under_the_share_id_and_can_mark_it_read()
    {
        var (anna, annaName, space) = await AnnaWithProjectAsync();
        var (ben, benName, _) = await UserAsync("ben");
        var shareId = (await (await InviteAsync(anna, space, "Projekte", benName, "editor")).Content.ReadFromJsonAsync<InviteeInfo>())!.ShareId;

        var note = Assert.Single((await ben.Http.GetFromJsonAsync<NotificationsResponse>("/api/v1/notifications"))!.Notifications);
        using var read = await ben.Http.PostAsync($"/api/v1/notifications/{note.Id}/read", null);
        var after = Assert.Single((await ben.Http.GetFromJsonAsync<NotificationsResponse>("/api/v1/notifications"))!.Notifications);

        Assert.Equal(("shared", shareId, false), (note.Type, note.SpaceId, note.Read));
        Assert.Equal(annaName, note.Actor);
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        Assert.True(after.Read);
        Assert.Empty((await anna.Http.GetFromJsonAsync<NotificationsResponse>("/api/v1/notifications"))!.Notifications);
    }
}
