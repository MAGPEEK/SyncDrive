using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Anmeldeseite: Servername, Text und Hintergrundbild lesen alle ohne Anmeldung, ändern nur Administratoren.</summary>
public class PortalApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private static ByteArrayContent Image(byte[] data, string type = "image/png")
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return content;
    }

    [Fact]
    public async Task An_admin_sets_name_text_and_background_which_everyone_sees_before_signing_in()
    {
        var admin = await factory.LoginAsync();
        var anonymous = factory.CreateClient();

        var updated = await admin.Http.PutAsJsonAsync("/api/v1/web/portal", new PortalUpdateRequest("  Base Camp ", "Trust in yourself"));
        var uploaded = await admin.Http.PutAsync("/api/v1/web/portal/background", Image(Png));
        var info = (await anonymous.GetFromJsonAsync<PortalInfo>("/api/v1/web/portal"))!;
        var background = await anonymous.GetAsync("/" + info.BackgroundUrl);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (updated.StatusCode, uploaded.StatusCode));
        Assert.Equal(("Base Camp", "Trust in yourself"), (info.ServerName, info.LoginMessage));
        Assert.StartsWith("api/v1/web/portal/background?v=", info.BackgroundUrl, StringComparison.Ordinal);
        Assert.Equal(Png, await background.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", background.Content.Headers.ContentType?.MediaType);

        var removed = await admin.Http.DeleteAsync("/api/v1/web/portal/background");
        Assert.Null((await removed.Content.ReadFromJsonAsync<PortalInfo>())!.BackgroundUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/web/portal/background")).StatusCode);
    }

    [Fact]
    public async Task Only_admins_change_the_portal_and_only_real_images_are_accepted()
    {
        var user = await factory.CreateUserAsync("portal-" + Guid.NewGuid().ToString("N")[..8]);
        var normal = await factory.LoginAsync(user, "another-long-password");
        var admin = await factory.LoginAsync();
        var anonymous = factory.CreateClient();

        var byUser = await normal.Http.PutAsJsonAsync("/api/v1/web/portal", new PortalUpdateRequest("Gekapert", null));
        var byAnonymous = await anonymous.PutAsJsonAsync("/api/v1/web/portal", new PortalUpdateRequest("Gekapert", null));
        var script = await admin.Http.PutAsync("/api/v1/web/portal/background", Image("<svg onload=alert(1)>"u8.ToArray(), "image/png"));
        var tooLarge = await admin.Http.PutAsync("/api/v1/web/portal/background", Image([.. Png, .. new byte[5 * 1024 * 1024]]));
        var emptyName = await admin.Http.PutAsJsonAsync("/api/v1/web/portal", new PortalUpdateRequest("  ", null));

        Assert.Equal(HttpStatusCode.Forbidden, byUser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, byAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, script.StatusCode); // am Inhalt erkannt, nicht an der Typangabe
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);
        Assert.NotEqual("Gekapert", (await anonymous.GetFromJsonAsync<PortalInfo>("/api/v1/web/portal"))!.ServerName);
    }
}
