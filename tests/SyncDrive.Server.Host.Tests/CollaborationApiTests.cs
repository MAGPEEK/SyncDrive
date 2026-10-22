using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>M4b über HTTP: Geräte, Rollen, Sperren, Benachrichtigungen, Versionen, Papierkorb, Kopieren, Favoriten, Labels.</summary>
public class CollaborationApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string BenPassword = "another-long-password";
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static string HashOf(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task<CommitResponse> PutAsync(ApiClient api, string space, string path, string text, long baseRevision = 0)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using (var body = new ByteArrayContent(bytes))
        {
            (await api.Http.PutAsync($"/api/v1/blocks/{HashOf(bytes)}", body)).EnsureSuccessStatusCode();
        }

        var response = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, path, baseRevision, Blocks: [HashOf(bytes)], Size: bytes.Length, MTime: T0, FileHash: HashOf(bytes)));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CommitResponse>())!;
    }

    private static Task<HttpResponseMessage> CommitAsync(ApiClient api, string space, CommitRequest request) =>
        api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", request);

    private static async Task<NodeDetail> NodeAsync(ApiClient api, string space, string path) =>
        (await api.Http.GetFromJsonAsync<NodeDetail>($"/api/v1/spaces/{space}/node?path={Uri.EscapeDataString(path)}"))!;

    /// <summary>Anna (Besitzerin ihres Space) und Ben mit der gewünschten Rolle darin.</summary>
    private async Task<(ApiClient Anna, ApiClient Ben, string Space)> TeamAsync(string benRole)
    {
        var anna = await factory.LoginAsync();
        var space = await anna.FirstSpaceIdAsync();
        var benName = await factory.CreateUserAsync(Unique("ben"));
        var add = await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/members", new AddMemberRequest(benName, benRole));
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
        return (anna, await factory.LoginAsync(benName, BenPassword), space);
    }

    [Fact]
    public async Task The_device_list_shows_client_type_version_and_marks_the_current_device()
    {
        var client = factory.CreateClient();
        var registered = await client.PostAsJsonAsync("/api/v1/auth/devices",
            new DeviceRegistrationRequest(HostFactory.AdminName, HostFactory.AdminPassword, "Laptop", "windows", "1.0.0"));
        var token = (await registered.Content.ReadFromJsonAsync<DeviceRegistrationResponse>())!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        var devices = (await client.GetFromJsonAsync<DevicesResponse>("/api/v1/auth/devices"))!.Devices;

        var mine = Assert.Single(devices, d => d.IsCurrent);
        Assert.Equal((token.DeviceId, "Laptop", "windows", "1.0.0"), (mine.DeviceId, mine.Name, mine.ClientType, mine.ClientVersion));
        Assert.NotNull(mine.LastSeen);
    }

    [Fact]
    public async Task A_member_sees_the_shared_space_with_their_role_and_editors_cannot_share()
    {
        var (_, ben, space) = await TeamAsync("editor");

        var spaces = (await ben.Http.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!.Spaces;
        var share = await ben.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/members", new AddMemberRequest(HostFactory.AdminName, "viewer"));

        Assert.Contains(spaces, s => s.Id == space && s.Role == "editor");
        Assert.Equal(HttpStatusCode.Forbidden, share.StatusCode);
    }

    [Fact]
    public async Task A_previewer_sees_metadata_but_no_blocks_and_cannot_change_anything()
    {
        var (anna, ben, space) = await TeamAsync("previewer");
        var path = Unique("vorschau") + ".txt";
        await PutAsync(anna, space, path, "geheimer Inhalt");

        var node = await NodeAsync(ben, space, path);
        var versions = await ben.Http.GetAsync($"/api/v1/spaces/{space}/versions?path={path}");
        var commit = await CommitAsync(ben, space, new CommitRequest(CommitOps.Mkdir, Unique("x"), 0, MTime: T0));

        Assert.Equal(path, node.Node.Path);
        Assert.Empty(node.Blocks);
        Assert.Equal(HttpStatusCode.Forbidden, versions.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, commit.StatusCode);
    }

    [Fact]
    public async Task Versions_can_be_listed_and_restored_over_http()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var path = Unique("bericht") + ".txt";
        var v1 = await PutAsync(api, space, path, "erste Fassung");
        var hashV1 = (await NodeAsync(api, space, path)).Node.Hash;
        var v2 = await PutAsync(api, space, path, "zweite Fassung", v1.Revision);

        var versions = (await api.Http.GetFromJsonAsync<VersionsResponse>($"/api/v1/spaces/{space}/versions?path={path}"))!.Versions;
        var restore = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/versions/restore", new RestoreVersionRequest(path, v1.Revision, v2.Revision));

        Assert.Equal([v2.Revision, v1.Revision], versions.Select(v => v.Revision));
        Assert.All(versions, v => Assert.Equal(HostFactory.AdminName, v.CreatedBy)); // der Name, nicht die interne Benutzer-ID
        Assert.True(restore.IsSuccessStatusCode);
        Assert.Equal(hashV1, (await NodeAsync(api, space, path)).Node.Hash);
    }

    [Fact]
    public async Task Deleted_items_appear_in_the_trash_and_can_be_restored_over_http()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var dir = Unique("ordner");
        await CommitAsync(api, space, new CommitRequest(CommitOps.Mkdir, dir, 0, MTime: T0));
        var file = await PutAsync(api, space, $"{dir}/innen.txt", "i");
        (await CommitAsync(api, space, new CommitRequest(CommitOps.Delete, dir, file.Revision))).EnsureSuccessStatusCode();

        var trash = (await api.Http.GetFromJsonAsync<TrashResponse>($"/api/v1/spaces/{space}/trash"))!.Entries;
        var entry = Assert.Single(trash, e => e.OriginalPath == dir);
        var restored = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/trash/restore", new RestoreTrashRequest(entry.NodeId));

        Assert.True(restored.IsSuccessStatusCode);
        Assert.Equal("i".Length, (await NodeAsync(api, space, $"{dir}/innen.txt")).Node.Size);
    }

    [Fact]
    public async Task Copy_over_http_needs_no_upload()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var path = Unique("vorlage") + ".txt";
        await PutAsync(api, space, path, "Vorlage");

        var copy = await CommitAsync(api, space, new CommitRequest(CommitOps.Copy, path, 0, NewPath: "Kopie " + path));

        Assert.True(copy.IsSuccessStatusCode);
        Assert.Equal((await NodeAsync(api, space, path)).Blocks, (await NodeAsync(api, space, "Kopie " + path)).Blocks);
    }

    [Fact]
    public async Task Stars_follow_renames_and_vanish_with_deletion()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var path = Unique("favorit") + ".txt";
        var created = await PutAsync(api, space, path, "f");

        Assert.Equal(HttpStatusCode.NoContent, (await api.Http.PutAsync($"/api/v1/spaces/{space}/star?path={path}", null)).StatusCode);
        var moved = await CommitAsync(api, space, new CommitRequest(CommitOps.Move, path, created.Revision, NewPath: "umbenannt-" + path));
        var afterMove = (await api.Http.GetFromJsonAsync<ItemsResponse>("/api/v1/stars"))!.Items;
        var movedRevision = (await moved.Content.ReadFromJsonAsync<CommitResponse>())!.Revision;
        await CommitAsync(api, space, new CommitRequest(CommitOps.Delete, "umbenannt-" + path, movedRevision));
        var afterDelete = (await api.Http.GetFromJsonAsync<ItemsResponse>("/api/v1/stars"))!.Items;

        Assert.Contains(afterMove, i => i.Path == "umbenannt-" + path && i.NodeId == created.NodeId);
        Assert.DoesNotContain(afterDelete, i => i.NodeId == created.NodeId);
    }

    [Fact]
    public async Task Labels_are_personal_assignable_and_deletable()
    {
        var (anna, ben, space) = await TeamAsync("editor");
        var path = Unique("rechnung") + ".pdf";
        await PutAsync(anna, space, path, "r");

        var created = await anna.Http.PostAsJsonAsync("/api/v1/labels", new CreateLabelRequest("Steuer", "#FF8800"));
        var label = (await created.Content.ReadFromJsonAsync<LabelInfo>())!;
        var assign = await anna.Http.PutAsync($"/api/v1/spaces/{space}/labels/{label.Id}?path={path}", null);
        var items = (await anna.Http.GetFromJsonAsync<ItemsResponse>($"/api/v1/labels/{label.Id}/items"))!.Items;
        var bensView = await ben.Http.GetAsync($"/api/v1/labels/{label.Id}/items");
        var bensLabels = (await ben.Http.GetFromJsonAsync<LabelsResponse>("/api/v1/labels"))!.Labels;

        Assert.Equal(("Steuer", "#ff8800"), (label.Name, label.Color));
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        Assert.Equal(path, Assert.Single(items).Path);
        Assert.Equal(HttpStatusCode.NotFound, bensView.StatusCode);
        Assert.DoesNotContain(bensLabels, l => l.Id == label.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await anna.Http.DeleteAsync($"/api/v1/labels/{label.Id}")).StatusCode);
    }

    [Fact]
    public async Task Sharing_a_space_notifies_the_new_member_with_who_shared_it()
    {
        var (anna, ben, space) = await TeamAsync("editor");

        var notes = (await ben.Http.GetFromJsonAsync<NotificationsResponse>("/api/v1/notifications"))!.Notifications;

        var shared = Assert.Single(notes, n => n.Type == "shared" && n.SpaceId == space);
        Assert.Equal((HostFactory.AdminName, false), (shared.Actor, shared.Read));
        Assert.DoesNotContain((await anna.Http.GetFromJsonAsync<NotificationsResponse>("/api/v1/notifications"))!.Notifications, n => n.Type == "shared"); // wer teilt, wird nicht benachrichtigt
    }

    [Fact]
    public async Task Usage_sums_current_file_sizes_below_a_folder_and_snapshot_depth_limits_levels()
    {
        var anna = await factory.LoginAsync();
        var space = await anna.FirstSpaceIdAsync();
        var folder = Unique("usage");
        (await CommitAsync(anna, space, new CommitRequest(CommitOps.Mkdir, folder, 0))).EnsureSuccessStatusCode();
        (await CommitAsync(anna, space, new CommitRequest(CommitOps.Mkdir, $"{folder}/tief", 0))).EnsureSuccessStatusCode();
        await PutAsync(anna, space, $"{folder}/a.txt", "12345");
        await PutAsync(anna, space, $"{folder}/tief/b.txt", "123");

        var usage = (await anna.Http.GetFromJsonAsync<UsageResponse>($"/api/v1/spaces/{space}/usage?path={folder}"))!;
        var flat = await anna.Http.GetStringAsync($"/api/v1/spaces/{space}/snapshot?path={folder}&depth=1");
        var bad = await anna.Http.GetAsync($"/api/v1/spaces/{space}/snapshot?path={folder}&depth=0");

        Assert.Equal((8L, 2L), (usage.Bytes, usage.Files));
        Assert.Equal(2, flat.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // a.txt und tief, nicht tief/b.txt
        Assert.DoesNotContain("b.txt", flat, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
