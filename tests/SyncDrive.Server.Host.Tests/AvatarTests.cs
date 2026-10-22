using System.Net;
using SkiaSharp;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Profilbild (0.30.0): hochladen, verkleinert lesen, nur das eigene, entfernen, falsche Daten ablehnen.</summary>
public class AvatarTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";

    private static ByteArrayContent Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Teal);
        }

        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 90);
        return new ByteArrayContent(data.ToArray());
    }

    private async Task<ApiClient> UserAsync(string prefix) =>
        await factory.LoginAsync(await factory.CreateUserAsync(prefix + "-" + Guid.NewGuid().ToString("N")[..8]), Password);

    [Fact]
    public async Task A_user_uploads_a_picture_that_is_stored_small_and_only_for_themselves()
    {
        var anna = await UserAsync("anna");
        var ben = await UserAsync("ben");

        var before = await anna.Http.GetAsync("/api/v1/account/avatar");
        using var put = await anna.Http.PutAsync("/api/v1/account/avatar", Png(1200, 800));
        using var get = await anna.Http.GetAsync("/api/v1/account/avatar");
        using var stored = SKBitmap.Decode(await get.Content.ReadAsByteArrayAsync());
        var bens = await ben.Http.GetAsync("/api/v1/account/avatar");

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(("image/jpeg", 256, 171), (get.Content.Headers.ContentType?.MediaType, stored.Width, stored.Height));
        Assert.Equal(HttpStatusCode.NotFound, bens.StatusCode); // jeder sieht nur sein eigenes Bild
    }

    [Fact]
    public async Task A_picture_can_be_removed_and_other_data_is_rejected()
    {
        var anna = await UserAsync("anna");
        await anna.Http.PutAsync("/api/v1/account/avatar", Png(64, 64));

        using var removed = await anna.Http.DeleteAsync("/api/v1/account/avatar");
        var after = await anna.Http.GetAsync("/api/v1/account/avatar");
        using var text = await anna.Http.PutAsync("/api/v1/account/avatar", new ByteArrayContent("kein Bild"u8.ToArray()));
        using var tooLarge = await anna.Http.PutAsync("/api/v1/account/avatar", new ByteArrayContent(new byte[6 * 1024 * 1024]));

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (removed.StatusCode, after.StatusCode));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (text.StatusCode, tooLarge.StatusCode));
    }
}
