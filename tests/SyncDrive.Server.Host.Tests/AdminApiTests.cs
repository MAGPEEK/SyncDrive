using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>M7 Admin-Konsole über HTTP: Benutzer anlegen, sperren, Passwort neu, Admin-Recht, Speicherkontingent.</summary>
public class AdminApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "ein-langes-passwort-1";

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<AdminUserInfo> CreateAsync(ApiClient admin, string name, bool isAdmin = false, long? quota = null)
    {
        using var response = await admin.Http.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(name, Password, isAdmin, quota));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AdminUserInfo>())!;
    }

    private static async Task<HttpResponseMessage> PutAsync(ApiClient api, string space, string path, string text, long baseRevision = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        using (var body = new ByteArrayContent(bytes))
        {
            (await api.Http.PutAsync($"/api/v1/blocks/{hash}", body)).EnsureSuccessStatusCode();
        }

        return await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, path, baseRevision, Blocks: [hash], Size: bytes.Length, MTime: DateTimeOffset.UtcNow, FileHash: hash));
    }

    private static Task<HttpResponseMessage> PatchAsync(ApiClient admin, string userId, UpdateUserRequest request) =>
        admin.Http.PatchAsJsonAsync($"/api/v1/admin/users/{userId}", request);

    private async Task<HttpStatusCode> SignInAsync(string name, string password)
    {
        using var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/devices", new DeviceRegistrationRequest(name, password, "Test"));
        return response.StatusCode;
    }

    [Fact]
    public async Task An_admin_creates_users_who_can_sign_in_and_only_admins_see_the_list()
    {
        var admin = await factory.LoginAsync();
        var name = Unique("lena");

        var created = await CreateAsync(admin, name, quota: 1000);
        var lena = await factory.LoginAsync(name, Password);
        var users = (await admin.Http.GetFromJsonAsync<AdminUsersResponse>("/api/v1/admin/users"))!.Users;
        using var asLena = await lena.Http.GetAsync("/api/v1/admin/users");
        using var lenaCreates = await lena.Http.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(Unique("x"), Password));
        using var twice = await admin.Http.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(name, Password));
        using var shortPassword = await admin.Http.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(Unique("y"), "kurz"));

        var listed = Assert.Single(users, u => u.Id == created.Id);
        Assert.Equal((name, false, false, 1000L, 0L), (listed.Name, listed.IsAdmin, listed.Disabled, listed.QuotaBytes, listed.UsedBytes));
        Assert.Equal(1, listed.Devices);
        Assert.NotNull(listed.CreatedAt);
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (asLena.StatusCode, lenaCreates.StatusCode));
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.BadRequest), (twice.StatusCode, shortPassword.StatusCode));
    }

    [Fact]
    public async Task Uploads_stop_at_the_quota_and_count_only_the_growth()
    {
        var admin = await factory.LoginAsync();
        var name = Unique("max");
        var max = await CreateAsync(admin, name, quota: 10);
        var api = await factory.LoginAsync(name, Password);
        var space = await api.FirstSpaceIdAsync();

        using var first = await PutAsync(api, space, "a.txt", "123456");
        using var tooMuch = await PutAsync(api, space, "b.txt", "123456");
        var problem = await tooMuch.Content.ReadFromJsonAsync<ProblemDetail>();
        var revision = (await first.Content.ReadFromJsonAsync<CommitResponse>())!.Revision;
        using var growing = await PutAsync(api, space, "a.txt", "12345678", revision); // nur 2 Byte mehr
        using var copy = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Copy, "a.txt", 0, NewPath: "kopie.txt"));
        (await PatchAsync(admin, max.Id, new UpdateUserRequest(ClearQuota: true))).EnsureSuccessStatusCode();
        using var unlimited = await PutAsync(api, space, "b.txt", "123456");
        var listed = (await admin.Http.GetFromJsonAsync<AdminUsersResponse>("/api/v1/admin/users"))!.Users.Single(u => u.Id == max.Id);

        Assert.True(first.IsSuccessStatusCode);
        Assert.Equal((HttpStatusCode.InsufficientStorage, ErrorCode.QuotaExceeded), (tooMuch.StatusCode, problem!.Code));
        Assert.True(growing.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.InsufficientStorage, copy.StatusCode);
        Assert.True(unlimited.IsSuccessStatusCode);
        Assert.Equal((14L, (long?)null), (listed.UsedBytes, listed.QuotaBytes));
    }

    [Fact]
    public async Task A_disabled_user_is_signed_out_everywhere_and_cannot_sign_in_until_enabled()
    {
        var admin = await factory.LoginAsync();
        var name = Unique("kai");
        var kai = await CreateAsync(admin, name);
        var session = await factory.LoginAsync(name, Password);

        using var disabled = await PatchAsync(admin, kai.Id, new UpdateUserRequest(Disabled: true));
        using var oldToken = await session.Http.GetAsync("/api/v1/spaces");
        var whileDisabled = await SignInAsync(name, Password);
        (await PatchAsync(admin, kai.Id, new UpdateUserRequest(Disabled: false))).EnsureSuccessStatusCode();
        var afterEnabled = await SignInAsync(name, Password);

        Assert.True((await disabled.Content.ReadFromJsonAsync<AdminUserInfo>())!.Disabled);
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (oldToken.StatusCode, whileDisabled));
        Assert.Equal(HttpStatusCode.OK, afterEnabled);
    }

    [Fact]
    public async Task A_new_password_signs_out_all_devices()
    {
        var admin = await factory.LoginAsync();
        var name = Unique("ute");
        var ute = await CreateAsync(admin, name);
        var session = await factory.LoginAsync(name, Password);

        using var tooShort = await admin.Http.PutAsJsonAsync($"/api/v1/admin/users/{ute.Id}/password", new SetPasswordRequest("kurz"));
        using var reset = await admin.Http.PutAsJsonAsync($"/api/v1/admin/users/{ute.Id}/password", new SetPasswordRequest("ganz-neues-passwort"));
        using var oldToken = await session.Http.GetAsync("/api/v1/spaces");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NoContent), (tooShort.StatusCode, reset.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.OK), (await SignInAsync(name, Password), await SignInAsync(name, "ganz-neues-passwort")));
    }

    [Fact]
    public async Task The_server_tells_its_version_and_devices_record_theirs()
    {
        var info = await factory.CreateClient().GetFromJsonAsync<ServerInfo>("/api/v1/info");
        var web = factory.CreateClient();
        web.DefaultRequestHeaders.Add("X-SyncDrive-Web", "1");
        using var signIn = await web.PostAsJsonAsync("/api/v1/web/session", new WebSignInRequest(HostFactory.AdminName, HostFactory.AdminPassword));
        var cookie = signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        web.DefaultRequestHeaders.Add("Cookie", cookie);
        var devices = (await web.GetFromJsonAsync<DevicesResponse>("/api/v1/auth/devices"))!.Devices;

        Assert.Equal(("SyncDrive", ProductVersion.Current), (info!.Product, info.Version));
        Assert.Equal(ProductVersion.Current, devices.Single(d => d.IsCurrent).ClientVersion);
    }

    [Fact]
    public async Task Admins_cannot_lock_themselves_out_and_admin_rights_take_effect_at_once()
    {
        var admin = await factory.LoginAsync();
        var name = Unique("chef");
        var chef = await CreateAsync(admin, name, isAdmin: true);
        var chefApi = await factory.LoginAsync(name, Password);

        using var selfDemote = await PatchAsync(chefApi, chef.Id, new UpdateUserRequest(IsAdmin: false));
        using var selfDisable = await PatchAsync(chefApi, chef.Id, new UpdateUserRequest(Disabled: true));
        using var chefLists = await chefApi.Http.GetAsync("/api/v1/admin/users");
        (await PatchAsync(admin, chef.Id, new UpdateUserRequest(IsAdmin: false))).EnsureSuccessStatusCode();
        using var afterDemotion = await chefApi.Http.GetAsync("/api/v1/admin/users");
        using var negative = await PatchAsync(admin, chef.Id, new UpdateUserRequest(QuotaBytes: -1));
        using var unknown = await PatchAsync(admin, "u_gibt-es-nicht", new UpdateUserRequest(Disabled: true));

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (selfDemote.StatusCode, selfDisable.StatusCode));
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Forbidden), (chefLists.StatusCode, afterDemotion.StatusCode));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (negative.StatusCode, unknown.StatusCode));
    }
}
