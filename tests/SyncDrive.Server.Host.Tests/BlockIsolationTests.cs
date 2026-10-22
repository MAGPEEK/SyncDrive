using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>M7 Härtung (0.15.0) über HTTP: Wer nur den Hash einer fremden Datei kennt, erfährt nichts über sie und kommt nicht an sie heran.</summary>
public class BlockIsolationTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";

    private async Task<(ApiClient Api, string Space)> UserAsync(string prefix)
    {
        var api = await factory.LoginAsync(await factory.CreateUserAsync(prefix + "-" + Guid.NewGuid().ToString("N")[..8]), Password);
        return (api, await api.FirstSpaceIdAsync());
    }

    private static string HashOf(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    private static CommitRequest PutRequest(string path, byte[] content) => new(
        CommitOps.Put, path, 0, Blocks: [HashOf(content)], Size: content.Length, MTime: DateTimeOffset.UtcNow, FileHash: HashOf(content));

    [Fact]
    public async Task A_foreign_block_is_invisible_and_cannot_be_committed_without_uploading_it()
    {
        var (anna, annaSpace) = await UserAsync("anna");
        var (ben, benSpace) = await UserAsync("ben");
        var secret = Encoding.UTF8.GetBytes("Annas Geheimnis " + Guid.NewGuid());
        using (var body = new ByteArrayContent(secret))
        {
            (await anna.Http.PutAsync($"/api/v1/blocks/{HashOf(secret)}", body)).EnsureSuccessStatusCode();
        }

        (await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{annaSpace}/commit", PutRequest("geheim.txt", secret))).EnsureSuccessStatusCode();

        using var missing = await ben.Http.PostAsJsonAsync($"/api/v1/spaces/{benSpace}/blocks/missing", new MissingBlocksRequest([HashOf(secret)]));
        using var viaOwnSpace = await ben.Http.GetAsync($"/api/v1/spaces/{benSpace}/blocks/{HashOf(secret)}");
        using var viaAnnasSpace = await ben.Http.GetAsync($"/api/v1/spaces/{annaSpace}/blocks/{HashOf(secret)}");
        using var stolen = await ben.Http.PostAsJsonAsync($"/api/v1/spaces/{benSpace}/commit", PutRequest("gestohlen.txt", secret));
        using var annaReads = await anna.Http.GetAsync($"/api/v1/spaces/{annaSpace}/blocks/{HashOf(secret)}");

        Assert.Equal([HashOf(secret)], (await missing.Content.ReadFromJsonAsync<MissingBlocksResponse>())!.Missing);
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (viaOwnSpace.StatusCode, viaAnnasSpace.StatusCode));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stolen.StatusCode);
        Assert.Equal(secret, await annaReads.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_previewer_sees_the_structure_but_cannot_download_blocks()
    {
        var (anna, annaSpace) = await UserAsync("anna");
        var benName = await factory.CreateUserAsync("ben-" + Guid.NewGuid().ToString("N")[..8]);
        var content = Encoding.UTF8.GetBytes("Vorschau " + Guid.NewGuid());
        using (var body = new ByteArrayContent(content))
        {
            (await anna.Http.PutAsync($"/api/v1/blocks/{HashOf(content)}", body)).EnsureSuccessStatusCode();
        }

        (await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{annaSpace}/commit", new CommitRequest(CommitOps.Mkdir, "Ordner", 0, MTime: DateTimeOffset.UtcNow)))
            .EnsureSuccessStatusCode();
        (await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{annaSpace}/commit", PutRequest("Ordner/datei.txt", content))).EnsureSuccessStatusCode();
        var invite = await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{annaSpace}/sharing/invitees?path=Ordner", new InviteRequest(benName, "previewer"));
        var shareId = (await invite.Content.ReadFromJsonAsync<InviteeInfo>())!.ShareId;
        var ben = await factory.LoginAsync(benName, Password);

        using var block = await ben.Http.GetAsync($"/api/v1/spaces/{shareId}/blocks/{HashOf(content)}");
        var node = (await ben.Http.GetFromJsonAsync<NodeDetail>($"/api/v1/spaces/{shareId}/node?path=datei.txt"))!;

        Assert.Equal(HttpStatusCode.Forbidden, block.StatusCode);
        Assert.Empty(node.Blocks);
    }
}
