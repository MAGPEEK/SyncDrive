using Microsoft.AspNetCore.Mvc.Testing;
using SyncDrive.Contracts;
using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>Anmeldeseite: zwei Schritte, Servername und Hintergrund vom Administrator.</summary>
public sealed class PortalTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private SessionViewModel NewSession() =>
        new(new HttpDriveApi(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true })), WebTexts.English);

    [Fact]
    public async Task Signing_in_takes_name_and_password_together()
    {
        var session = NewSession();
        await session.CheckAsync(default);

        var empty = await session.SignInAsync("   ", HostFactory.AdminPassword, default);
        var wrong = await session.SignInAsync("admin", "falsch", default);
        var wrongError = session.Error;
        var right = await session.SignInAsync(" admin ", HostFactory.AdminPassword, default);

        Assert.Equal((false, false, WebTexts.English.SignInFailed), (empty, wrong, wrongError));
        Assert.True(right);
        Assert.Null(session.Error);
        Assert.Equal("admin", session.Session!.UserName);
    }

    [Fact]
    public async Task An_admin_designs_the_portal_and_the_sign_in_page_shows_it()
    {
        var admin = await WebPage.OpenAsync(factory, HostFactory.AdminName, HostFactory.AdminPassword);
        var portal = new PortalViewModel(admin.Api, WebTexts.English);
        PortalInfo? saved = null;
        portal.Saved += p => saved = p;
        await portal.LoadAsync(default);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46];

        Assert.True(await portal.SaveAsync("Base Camp", "Trust in yourself", default));
        Assert.True(await portal.SetBackgroundAsync(WebPage.File("hintergrund.jpg", jpeg), default));
        var tooLarge = await portal.SetBackgroundAsync(
            new UploadFile("riesig.jpg", PortalViewModel.MaxBackgroundBytes + 1, DateTimeOffset.UtcNow, () => new MemoryStream()), default);
        var visitor = NewSession();
        await visitor.CheckAsync(default);

        Assert.Equal(("Base Camp", "Trust in yourself"), (visitor.Portal.ServerName, visitor.Portal.LoginMessage));
        Assert.NotNull(visitor.Portal.BackgroundUrl);
        Assert.Equal(jpeg, await admin.Http.GetByteArrayAsync(visitor.Portal.BackgroundUrl));
        Assert.Equal(visitor.Portal, saved);
        Assert.Equal((false, "The image is larger than 5 MB."), (tooLarge, portal.Message));
    }

    [Fact]
    public async Task A_normal_user_cannot_change_the_portal()
    {
        var page = await WebPage.OpenAsync(factory);
        var portal = new PortalViewModel(page.Api, WebTexts.English);

        var ok = await portal.SaveAsync("Gekapert", null, default);

        Assert.Equal((false, "You do not have permission for this action."), (ok, portal.Message));
    }
}
