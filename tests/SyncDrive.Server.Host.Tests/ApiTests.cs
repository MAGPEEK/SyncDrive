using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

public class ApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static string HashOf(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task<ProblemDetail> ProblemOf(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<ProblemDetail>())!;
    }

    private static async Task<HttpResponseMessage> UploadAsync(ApiClient api, byte[] content)
    {
        using var body = new ByteArrayContent(content);
        return await api.Http.PutAsync($"/api/v1/blocks/{HashOf(content)}", body);
    }

    private static CommitRequest PutRequest(string path, byte[] content, long baseRevision = 0) => new(
        CommitOps.Put, path, baseRevision, Blocks: [HashOf(content)], Size: content.Length, MTime: T0,
        FileHash: HashOf(content));

    [Fact]
    public async Task Health_check_needs_no_login()
    {
        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_valid_token_are_unauthorized()
    {
        var client = factory.CreateClient();
        var anonymous = await client.GetAsync("/api/v1/spaces");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sd_nope");
        var wrong = await client.GetAsync("/api/v1/spaces");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Bearer", anonymous.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(ErrorCode.Unauthorized, (await ProblemOf(wrong)).Code);
    }

    [Fact]
    public async Task Device_registration_rejects_wrong_password_and_unknown_user_alike()
    {
        var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/devices", new DeviceRegistrationRequest(HostFactory.AdminName, "wrong-password-x", "PC"));
        var unknownUser = await client.PostAsJsonAsync(
            "/api/v1/auth/devices", new DeviceRegistrationRequest("ghost", "wrong-password-x", "PC"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal((await ProblemOf(wrongPassword)).Title, (await ProblemOf(unknownUser)).Title);
    }

    [Fact]
    public async Task Device_registration_with_empty_password_is_unauthorized_not_a_server_error()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/devices", new DeviceRegistrationRequest(HostFactory.AdminName, string.Empty, "PC"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_from_configuration_gets_a_personal_space()
    {
        var api = await factory.LoginAsync();

        var spaces = (await api.Http.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!;

        var space = Assert.Single(spaces.Spaces);
        Assert.Equal(("Meine Dateien", "owner"), (space.Name, space.Role));
    }

    [Fact]
    public async Task Full_upload_commit_feed_and_download_round_trip()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var path = Unique("report") + ".txt";
        byte[] content = [.. "Hallo Welt"u8];

        var missing = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/blocks/missing", new MissingBlocksRequest([HashOf(content)]));
        Assert.Equal([HashOf(content)], (await missing.Content.ReadFromJsonAsync<MissingBlocksResponse>())!.Missing);

        var early = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", PutRequest(path, content));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal(ErrorCode.BlocksMissing, (await ProblemOf(early)).Code);

        Assert.Equal(HttpStatusCode.NoContent, (await UploadAsync(api, content)).StatusCode);
        var commit = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", PutRequest(path, content));
        commit.EnsureSuccessStatusCode();
        var committed = (await commit.Content.ReadFromJsonAsync<CommitResponse>())!;

        var feed = (await api.Http.GetFromJsonAsync<ChangesResponse>($"/api/v1/spaces/{space}/changes?after=0"))!;
        var entry = Assert.Single(feed.Changes, c => c.Path == path);
        Assert.Equal((committed.Revision, ChangeOps.Upsert, HashOf(content)), (entry.Revision, entry.Op, entry.Hash));

        Assert.Equal(content, await api.Http.GetByteArrayAsync($"/api/v1/spaces/{space}/blocks/{HashOf(content)}"));
    }

    [Fact]
    public async Task Block_download_supports_range_requests()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        byte[] content = [.. "0123456789"u8];
        await UploadAsync(api, content);
        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", PutRequest(Unique("bereich") + ".txt", content))).EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/spaces/{space}/blocks/{HashOf(content)}");
        request.Headers.Range = new RangeHeaderValue(4, 6);
        var response = await api.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("456", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Block_upload_with_wrong_hash_is_rejected_and_unknown_block_is_not_found()
    {
        var api = await factory.LoginAsync();
        var claimed = HashOf([1, 2, 3]);

        using var body = new ByteArrayContent([9, 9, 9]);
        var upload = await api.Http.PutAsync($"/api/v1/blocks/{claimed}", body);
        var download = await api.Http.GetAsync($"/api/v1/spaces/{await api.FirstSpaceIdAsync()}/blocks/{claimed}");

        Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        Assert.Equal(ErrorCode.BlockHashMismatch, (await ProblemOf(upload)).Code);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact]
    public async Task Stale_commit_returns_conflict_with_current_state()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var path = Unique("conflict") + ".txt";
        byte[] v1 = [1];
        byte[] v2 = [2];
        await UploadAsync(api, v1);
        await UploadAsync(api, v2);
        var first = await (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", PutRequest(path, v1)))
            .Content.ReadFromJsonAsync<CommitResponse>();

        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", PutRequest(path, v2, first!.Revision)))
            .EnsureSuccessStatusCode();
        var stale = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", PutRequest(path, v1, first.Revision));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await ProblemOf(stale);
        Assert.Equal(ErrorCode.RevisionConflict, problem.Code);
        Assert.Equal(HashOf(v2), problem.Current!.Hash);
    }

    [Fact]
    public async Task Idempotency_key_makes_retries_safe()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var name = Unique("dir");
        var request = new CommitRequest(CommitOps.Mkdir, name, 0, MTime: T0);

        async Task<CommitResponse> Send()
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/spaces/{space}/commit")
            {
                Content = JsonContent.Create(request),
            };
            message.Headers.Add("Idempotency-Key", "retry-" + name);
            var response = await api.Http.SendAsync(message);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CommitResponse>())!;
        }

        Assert.Equal(await Send(), await Send());
    }

    [Fact]
    public async Task Other_users_cannot_see_or_touch_a_foreign_space()
    {
        var owner = await factory.LoginAsync();
        var space = await owner.FirstSpaceIdAsync();
        var bobName = await factory.CreateUserAsync(Unique("bob"));
        var bob = await factory.LoginAsync(bobName, "another-long-password");

        var feed = await bob.Http.GetAsync($"/api/v1/spaces/{space}/changes");
        var commit = await bob.Http.PostAsJsonAsync(
            $"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, "x", 0, MTime: T0));

        Assert.Equal(HttpStatusCode.NotFound, feed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, commit.StatusCode);
        var bobSpaces = (await bob.Http.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!;
        Assert.DoesNotContain(bobSpaces.Spaces, s => s.Id == space);
    }

    [Fact]
    public async Task Revoked_device_loses_access_immediately()
    {
        var api = await factory.LoginAsync();

        var revoke = await api.Http.DeleteAsync($"/api/v1/auth/devices/{api.Registration.DeviceId}");
        var after = await api.Http.GetAsync("/api/v1/spaces");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task A_device_cannot_revoke_someone_elses_device()
    {
        var admin = await factory.LoginAsync();
        var bobName = await factory.CreateUserAsync(Unique("bob"));
        var bob = await factory.LoginAsync(bobName, "another-long-password");

        var attempt = await bob.Http.DeleteAsync($"/api/v1/auth/devices/{admin.Registration.DeviceId}");

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync("/api/v1/spaces")).StatusCode);
    }

    [Fact]
    public async Task Long_poll_returns_as_soon_as_another_client_commits()
    {
        var waiter = await factory.LoginAsync();
        var writer = await factory.LoginAsync();
        var space = await waiter.FirstSpaceIdAsync();
        var latest = (await waiter.Http.GetFromJsonAsync<ChangesResponse>($"/api/v1/spaces/{space}/changes?after=0&limit=1000"))!.NextCursor;
        var name = Unique("wakeup");

        var pending = waiter.Http.GetFromJsonAsync<ChangesResponse>(
            $"/api/v1/spaces/{space}/changes/wait?after={latest}&timeout=20");
        await Task.Delay(300);
        Assert.False(pending.IsCompleted);
        (await writer.Http.PostAsJsonAsync(
            $"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, name, 0, MTime: T0))).EnsureSuccessStatusCode();

        var woken = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(name, Assert.Single(woken!.Changes).Path);
    }

    [Fact]
    public async Task Long_poll_without_news_returns_an_empty_page_after_the_timeout()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var latest = (await api.Http.GetFromJsonAsync<ChangesResponse>($"/api/v1/spaces/{space}/changes?after=0&limit=1000"))!.NextCursor;

        var page = await api.Http.GetFromJsonAsync<ChangesResponse>(
            $"/api/v1/spaces/{space}/changes/wait?after={latest}&timeout=1");

        Assert.Empty(page!.Changes);
        Assert.Equal(latest, page.NextCursor);
    }

    [Fact]
    public async Task Node_endpoint_returns_blocks_and_snapshot_streams_ndjson_lines()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var dir = Unique("tree");
        byte[] content = [.. "snapshot"u8];
        await UploadAsync(api, content);
        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, dir, 0, MTime: T0))).EnsureSuccessStatusCode();
        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", PutRequest($"{dir}/f.txt", content))).EnsureSuccessStatusCode();

        var detail = await api.Http.GetFromJsonAsync<NodeDetail>($"/api/v1/spaces/{space}/node?path={Uri.EscapeDataString(dir + "/f.txt")}");
        var response = await api.Http.GetAsync($"/api/v1/spaces/{space}/snapshot?path={Uri.EscapeDataString(dir)}");
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal([HashOf(content)], detail!.Blocks);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var entry = System.Text.Json.JsonSerializer.Deserialize<SnapshotEntry>(Assert.Single(lines), System.Text.Json.JsonSerializerOptions.Web);
        Assert.Equal($"{dir}/f.txt", entry!.Path);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.GetAsync($"/api/v1/spaces/{space}/node?path=nope")).StatusCode);
    }

    [Fact]
    public async Task Invalid_paths_and_malformed_hashes_are_bad_requests()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();

        var path = await api.Http.PostAsJsonAsync(
            $"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, "../escape", 0, MTime: T0));
        var hash = await api.Http.GetAsync($"/api/v1/spaces/{space}/blocks/sha256:zzz");

        Assert.Equal(HttpStatusCode.BadRequest, path.StatusCode);
        Assert.Equal(ErrorCode.InvalidPath, (await ProblemOf(path)).Code);
        Assert.Equal(HttpStatusCode.BadRequest, hash.StatusCode);
    }
}

public class TlsTests
{
    [Fact]
    public async Task Plain_http_is_refused_but_the_health_check_stays_open()
    {
        await using var factory = new TlsRequiredFactory();
        var client = factory.CreateClient();

        var api = await client.GetAsync("/api/v1/spaces");
        var health = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.BadRequest, api.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Behind_a_trusted_tls_proxy_forwarded_proto_https_is_accepted()
    {
        await using var factory = new ProxiedTlsFactory();
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/spaces");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // TLS-Prüfung bestanden, Token fehlt
    }

    [Fact]
    public async Task Forwarded_proto_is_ignored_when_forwarded_headers_are_not_trusted()
    {
        await using var factory = new TlsRequiredFactory();
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/spaces");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
