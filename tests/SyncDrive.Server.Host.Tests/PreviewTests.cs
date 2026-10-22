using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SkiaSharp;
using SyncDrive.Contracts;
using SyncDrive.Infra.Imaging;

namespace SyncDrive.Server.Host.Tests;

/// <summary>M7 Vorschaubilder (0.17.0): Renderer (Größe, EXIF-Drehung, kaputte Daten) und der Endpunkt über HTTP.</summary>
public class PreviewTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";

    private static byte[] Image(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            canvas.DrawRect(0, 0, width / 2f, height / 2f, new SKPaint { Color = SKColors.Orange });
        }

        using var data = SKImage.FromBitmap(bitmap).Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>JPEG mit EXIF-Ausrichtung 6 („um 90° drehen“), wie es Telefone für Hochkant-Fotos schreiben.</summary>
    private static byte[] RotatedJpeg(int width, int height)
    {
        var jpeg = Image(width, height, SKEncodedImageFormat.Jpeg);
        byte[] tiff = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] exif = [.. "Exif\0\0"u8, .. tiff];
        var length = exif.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    private static SKBitmap Decode(byte[] jpeg) => SKBitmap.Decode(jpeg);

    [Fact]
    public void The_renderer_shrinks_to_the_longer_edge_and_keeps_the_aspect()
    {
        var thumbnail = new SkiaThumbnailRenderer().Render(new MemoryStream(Image(2000, 1000)), 128)!;
        using var decoded = Decode(thumbnail);

        Assert.Equal((128, 64), (decoded.Width, decoded.Height));
        Assert.Equal((byte)0xFF, thumbnail[0]); // JPEG
    }

    [Theory]
    [InlineData(1920, 1200, 1024, 640)] // ½ beim Dekodieren wäre 960: zu klein
    [InlineData(4000, 3000, 1024, 768)]
    [InlineData(4000, 3000, 128, 96)]
    public void Jpegs_reach_the_full_edge_although_decoded_in_steps(int width, int height, int edge, int expectedHeight)
    {
        using var decoded = Decode(new SkiaThumbnailRenderer().Render(new MemoryStream(Image(width, height, SKEncodedImageFormat.Jpeg)), edge)!);

        Assert.Equal((edge, expectedHeight), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void The_renderer_turns_photos_upright_and_never_enlarges()
    {
        using var rotated = Decode(new SkiaThumbnailRenderer().Render(new MemoryStream(RotatedJpeg(400, 200)), 128)!);
        using var small = Decode(new SkiaThumbnailRenderer().Render(new MemoryStream(Image(40, 30)), 128)!);

        Assert.Equal((64, 128), (rotated.Width, rotated.Height));
        Assert.Equal((40, 30), (small.Width, small.Height));
    }

    [Fact]
    public void Damaged_data_gives_no_preview()
    {
        Assert.Null(new SkiaThumbnailRenderer().Render(new MemoryStream([1, 2, 3, 4, 5]), 128));
    }

    private static string HashOf(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task PutAsync(ApiClient api, string space, string path, byte[] content)
    {
        using (var body = new ByteArrayContent(content))
        {
            (await api.Http.PutAsync($"/api/v1/blocks/{HashOf(content)}", body)).EnsureSuccessStatusCode();
        }

        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, path, 0, Blocks: [HashOf(content)], Size: content.Length, MTime: DateTimeOffset.UtcNow, FileHash: HashOf(content))))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Images_get_small_and_large_previews_others_and_strangers_get_none()
    {
        var anna = await factory.LoginAsync(await factory.CreateUserAsync("anna-" + Guid.NewGuid().ToString("N")[..8]), Password);
        var space = await anna.FirstSpaceIdAsync();
        await PutAsync(anna, space, "foto.png", Image(3000, 1500));
        await PutAsync(anna, space, "notiz.txt", "kein Bild"u8.ToArray());
        var stranger = await factory.LoginAsync(await factory.CreateUserAsync("eve-" + Guid.NewGuid().ToString("N")[..8]), Password);

        using var small = await anna.Http.GetAsync($"/api/v1/spaces/{space}/thumbnail?path=foto.png");
        var smallBytes = await small.Content.ReadAsByteArrayAsync();
        var large = await anna.Http.GetByteArrayAsync($"/api/v1/spaces/{space}/thumbnail?path=foto.png&size=large");
        var again = await anna.Http.GetByteArrayAsync($"/api/v1/spaces/{space}/thumbnail?path=foto.png"); // aus dem Zwischenspeicher
        using var text = await anna.Http.GetAsync($"/api/v1/spaces/{space}/thumbnail?path=notiz.txt");
        using var foreign = await stranger.Http.GetAsync($"/api/v1/spaces/{space}/thumbnail?path=foto.png");
        using var smallImage = Decode(smallBytes);
        using var largeImage = Decode(large);

        Assert.Equal(("image/jpeg", 128, 1024), (small.Content.Headers.ContentType!.MediaType, smallImage.Width, largeImage.Width));
        Assert.Equal(smallBytes, again);
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (text.StatusCode, foreign.StatusCode));
    }

    [Fact]
    public async Task Previewers_see_previews_but_cannot_download_the_file()
    {
        var anna = await factory.LoginAsync(await factory.CreateUserAsync("anna-" + Guid.NewGuid().ToString("N")[..8]), Password);
        var space = await anna.FirstSpaceIdAsync();
        (await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, "Bilder", 0, MTime: DateTimeOffset.UtcNow)))
            .EnsureSuccessStatusCode();
        await PutAsync(anna, space, "Bilder/foto.jpg", Image(800, 600, SKEncodedImageFormat.Jpeg));
        var benName = await factory.CreateUserAsync("ben-" + Guid.NewGuid().ToString("N")[..8]);
        var invite = await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/sharing/invitees?path=Bilder", new InviteRequest(benName, "previewer"));
        var shareId = (await invite.Content.ReadFromJsonAsync<InviteeInfo>())!.ShareId;
        var ben = await factory.LoginAsync(benName, Password);

        using var preview = await ben.Http.GetAsync($"/api/v1/spaces/{shareId}/thumbnail?path=foto.jpg&size=large");
        using var download = await ben.Http.GetAsync($"/api/v1/spaces/{shareId}/download?path=foto.jpg");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Forbidden), (preview.StatusCode, download.StatusCode));
    }
}
