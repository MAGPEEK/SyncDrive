using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>M7 Verwaltung im Browser gegen den echten Host: Benutzer anlegen, Einstellungen, sperren, Passwort; Kontingent beim Hochladen.</summary>
public sealed class AdminTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "ein-langes-passwort";

    private async Task<AdminViewModel> AdminAsync()
    {
        var page = await WebPage.OpenAsync(factory, HostFactory.AdminName, HostFactory.AdminPassword);
        var admin = new AdminViewModel(page.Api, WebTexts.English) { Me = HostFactory.AdminName };
        await admin.LoadAsync(default);
        return admin;
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task An_admin_creates_a_user_with_quota_and_changes_the_settings()
    {
        var admin = await AdminAsync();
        var name = Unique("nora");

        var tooShort = await admin.CreateAsync(Unique("x"), "kurz", isAdmin: false, quotaGb: null, default);
        var tooShortMessage = admin.Message;
        var created = await admin.CreateAsync(name, Password, isAdmin: false, quotaGb: 2, default);
        var createdMessage = admin.Message;
        var nora = admin.Selected!;
        var storage = admin.StorageText(nora);
        var twice = await admin.CreateAsync(name, Password, isAdmin: false, quotaGb: null, default);
        var twiceMessage = admin.Message;
        await admin.SaveSettingsAsync(nora, isAdmin: true, quotaGb: null, default);
        var changed = admin.Users.Single(u => u.Id == nora.Id);

        Assert.Equal((false, WebTexts.English.UserPasswordTooShort), (tooShort, tooShortMessage));
        Assert.True(created);
        Assert.Equal($"User “{name}” created.", createdMessage);
        Assert.Equal((name, 2L * 1024 * 1024 * 1024, 2.0), (nora.Name, nora.QuotaBytes, AdminViewModel.QuotaInGb(nora)));
        Assert.Equal("0 B of 2 GB", storage);
        Assert.Equal((false, WebTexts.English.UserExists), (twice, twiceMessage));
        Assert.Equal((true, (long?)null), (changed.IsAdmin, changed.QuotaBytes));
        Assert.Equal("0 B · unlimited", admin.StorageText(changed));
        Assert.Null(AdminViewModel.StorageShare(changed));
    }

    [Fact]
    public async Task The_own_account_is_protected_and_others_can_be_disabled_and_get_a_new_password()
    {
        var admin = await AdminAsync();
        var me = admin.Users.Single(admin.IsMe);
        var name = Unique("tom");
        await admin.CreateAsync(name, Password, isAdmin: false, quotaGb: null, default);
        var tom = admin.Selected!;
        var session = await WebPage.OpenAsync(factory, name, Password);

        var selfDisable = await admin.SetDisabledAsync(me, true, default);
        var selfDemote = await admin.SaveSettingsAsync(me, isAdmin: false, quotaGb: null, default);
        var selfMessage = admin.Message;
        admin.Select(me);
        var canToggleMe = admin.CanToggle;
        await admin.SetDisabledAsync(tom, true, default);
        var disabled = admin.Users.Single(u => u.Id == tom.Id);
        var signedOut = await session.Api.GetSessionAsync(default);
        await admin.SetDisabledAsync(tom, false, default);
        var shortPassword = await admin.SetPasswordAsync(tom, "kurz", default);
        var reset = await admin.SetPasswordAsync(tom, "ein-ganz-neues-passwort", default);

        Assert.Equal((false, false, WebTexts.English.CannotChangeSelf), (selfDisable, selfDemote, selfMessage));
        Assert.False(canToggleMe);
        Assert.True(disabled.Disabled);
        Assert.Null(signedOut); // die Browser-Sitzung des Gesperrten ist beendet
        Assert.Equal((false, true), (shortPassword, reset));
        Assert.Equal($"New password set for “{name}”. All of their devices have to sign in again.", admin.Message);
        Assert.False(admin.Users.Single(u => u.Id == tom.Id).Disabled);
    }

    [Fact]
    public async Task An_upload_beyond_the_quota_shows_a_plain_message()
    {
        var admin = await AdminAsync();
        var name = Unique("ida");
        await admin.CreateAsync(name, Password, isAdmin: false, quotaGb: 0.000001, default); // etwa 1 KB
        var ida = await WebPage.OpenAsync(factory, name, Password);

        await ida.Browser.UploadAsync([WebPage.File("klein.txt", "passt")], default);
        await ida.Browser.UploadAsync([WebPage.File("groß.bin", new byte[4000])], default);
        await admin.LoadAsync(default);

        Assert.Equal(["klein.txt"], ida.Browser.Items.Select(i => i.Name));
        Assert.Equal(WebTexts.English.QuotaExceeded, ida.Browser.Message);
        Assert.Equal(5, admin.Users.Single(u => u.Name == name).UsedBytes);
    }
}
