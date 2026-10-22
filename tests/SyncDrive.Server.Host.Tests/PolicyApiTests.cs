using System.Net;
using System.Net.Http.Json;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>M7 Richtlinien (0.16.0) über HTTP: Links und Dateianfragen an/aus, Passwortpflicht, Höchstdauer; Abschalten wirkt sofort.</summary>
public class PolicyApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";

    /// <summary>Jeder Test beginnt mit den Vorgaben (die Richtlinien gelten serverweit, die Tests einer Klasse teilen den Server).</summary>
    private async Task<ApiClient> AdminWithDefaultsAsync()
    {
        var admin = await factory.LoginAsync();
        (await admin.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies())).EnsureSuccessStatusCode();
        return admin;
    }

    private async Task<(ApiClient Api, string Space)> OwnerWithFolderAsync()
    {
        var api = await factory.LoginAsync(await factory.CreateUserAsync("anna-" + Guid.NewGuid().ToString("N")[..8]), Password);
        var space = await api.FirstSpaceIdAsync();
        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, "Ordner", 0, MTime: DateTimeOffset.UtcNow)))
            .EnsureSuccessStatusCode();
        return (api, space);
    }

    private static Task<HttpResponseMessage> LinkAsync(ApiClient api, string space, string kind, LinkRequest request) =>
        api.Http.PutAsJsonAsync($"/api/v1/spaces/{space}/sharing/link?path=Ordner&kind={kind}", request);

    [Fact]
    public async Task Everyone_reads_the_policies_but_only_admins_change_them_within_limits()
    {
        var admin = await AdminWithDefaultsAsync();
        var (anna, _) = await OwnerWithFolderAsync();

        var read = await anna.Http.GetFromJsonAsync<SharingPolicies>("/api/v1/policies");
        using var annaChanges = await anna.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies(PublicLinks: false));
        using var zeroDays = await admin.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies(LinkMaxDays: 0));
        using var saved = await admin.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies(LinkPasswordRequired: true, RequestMaxDays: 14));
        var after = await anna.Http.GetFromJsonAsync<SharingPolicies>("/api/v1/policies");

        Assert.Equal(new SharingPolicies(), read);
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.BadRequest), (annaChanges.StatusCode, zeroDays.StatusCode));
        Assert.True(saved.IsSuccessStatusCode);
        Assert.Equal(new SharingPolicies(LinkPasswordRequired: true, RequestMaxDays: 14), after);
    }

    [Fact]
    public async Task Switching_public_links_off_blocks_new_and_existing_links_at_once()
    {
        var admin = await AdminWithDefaultsAsync();
        var (anna, space) = await OwnerWithFolderAsync();
        using var created = await LinkAsync(anna, space, LinkKinds.Share, new LinkRequest(null));
        var link = (await created.Content.ReadFromJsonAsync<LinkInfo>())!;
        var visitor = factory.CreateClient();

        (await admin.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies(PublicLinks: false))).EnsureSuccessStatusCode();
        using var whileOff = await visitor.GetAsync($"/api/v1/public/{link.Token}");
        using var newLink = await LinkAsync(anna, space, LinkKinds.Share, new LinkRequest(null));
        using var requestStillOk = await LinkAsync(anna, space, LinkKinds.Request, new LinkRequest(null));
        (await admin.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies())).EnsureSuccessStatusCode();
        using var onAgain = await visitor.GetAsync($"/api/v1/public/{link.Token}");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.Forbidden), (whileOff.StatusCode, newLink.StatusCode));
        Assert.True(requestStillOk.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, onAgain.StatusCode);
    }

    [Fact]
    public async Task Links_must_carry_the_required_password_and_expire_in_time()
    {
        var admin = await AdminWithDefaultsAsync();
        var (anna, space) = await OwnerWithFolderAsync();
        (await admin.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies(LinkPasswordRequired: true, LinkMaxDays: 7))).EnsureSuccessStatusCode();
        var soon = DateTimeOffset.UtcNow.AddDays(5);

        using var bare = await LinkAsync(anna, space, LinkKinds.Share, new LinkRequest(soon));
        using var noExpiry = await LinkAsync(anna, space, LinkKinds.Share, new LinkRequest(null, "geheim1"));
        using var tooLong = await LinkAsync(anna, space, LinkKinds.Share, new LinkRequest(DateTimeOffset.UtcNow.AddDays(30), "geheim1"));
        using var fine = await LinkAsync(anna, space, LinkKinds.Share, new LinkRequest(soon, "geheim1"));
        using var requestFree = await LinkAsync(anna, space, LinkKinds.Request, new LinkRequest(null)); // Dateianfragen haben eigene Regeln

        Assert.Equal(
            (HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest),
            (bare.StatusCode, noExpiry.StatusCode, tooLong.StatusCode));
        Assert.True(fine.IsSuccessStatusCode, await fine.Content.ReadAsStringAsync());
        Assert.True(requestFree.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Switching_file_requests_off_closes_upload_links_but_not_view_links()
    {
        var admin = await AdminWithDefaultsAsync();
        var (anna, space) = await OwnerWithFolderAsync();
        var request = (await (await LinkAsync(anna, space, LinkKinds.Request, new LinkRequest(null))).Content.ReadFromJsonAsync<LinkInfo>())!;
        var share = (await (await LinkAsync(anna, space, LinkKinds.Share, new LinkRequest(null))).Content.ReadFromJsonAsync<LinkInfo>())!;
        var visitor = factory.CreateClient();

        (await admin.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies(FileRequests: false))).EnsureSuccessStatusCode();
        using var upload = await visitor.PutAsync($"/api/v1/public/{request.Token}/files?name=x.txt", new StringContent("x"));
        using var view = await visitor.GetAsync($"/api/v1/public/{share.Token}");
        (await admin.Http.PutAsJsonAsync("/api/v1/admin/policies", new SharingPolicies())).EnsureSuccessStatusCode();

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.OK), (upload.StatusCode, view.StatusCode));
    }
}
