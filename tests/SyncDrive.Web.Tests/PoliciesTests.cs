using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using SyncDrive.Contracts;
using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>M7 Richtlinien (0.16.0) im Browser: die Verwaltung setzt sie, Freigabedialog und Menüs richten sich danach.</summary>
public sealed class PoliciesTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private async Task<PoliciesViewModel> AdminAsync()
    {
        var admin = await WebPage.OpenAsync(factory, HostFactory.AdminName, HostFactory.AdminPassword);
        var policies = new PoliciesViewModel(admin.Api, WebTexts.English);
        await policies.LoadAsync(default);
        return policies;
    }

    [Fact]
    public async Task The_share_dialog_follows_password_and_validity_rules()
    {
        var policies = await AdminAsync();
        var invalid = await policies.SaveAsync(new SharingPolicies(LinkMaxDays: 0), default);
        var invalidMessage = policies.Message;
        await policies.SaveAsync(new SharingPolicies(LinkPasswordRequired: true, LinkMaxDays: 7), default);
        var anna = await WebPage.OpenAsync(factory);
        await anna.Browser.CreateFolderAsync("Projekte", default);
        var share = new ShareViewModel(anna.Api, WebTexts.English, TimeProvider.System);

        await share.OpenAsync(anna.Browser.Space!, anna.Browser.Items.Single(), default);
        var editor = share.ShareLink;
        var forced = (editor.PasswordRequired, editor.UsePassword, editor.UseExpiry, editor.LatestDate);
        editor.Enabled = true;
        var noPassword = await share.SaveLinkAsync(editor, default);
        var noPasswordMessage = share.Message;
        editor.Password = "geheim1";
        editor.ExpiryDate = DateTime.Today.AddDays(20);
        var tooLate = await share.SaveLinkAsync(editor, default);
        var tooLateMessage = share.Message;
        editor.ExpiryDate = DateTime.Today.AddDays(7);
        var fine = await share.SaveLinkAsync(editor, default);
        await policies.SaveAsync(new SharingPolicies(), default);

        Assert.Equal((false, WebTexts.English.InvalidDays), (invalid, invalidMessage));
        Assert.Equal((true, true, true, (DateTime?)DateTime.Today.AddDays(7)), forced);
        Assert.Equal((false, WebTexts.English.PasswordMissing), (noPassword, noPasswordMessage));
        Assert.Equal((false, "The administrator allows at most 7 days. Please choose an earlier date."), (tooLate, tooLateMessage));
        Assert.True(fine, share.Message);
        Assert.True(editor.Link!.HasPassword);
    }

    [Fact]
    public async Task Switching_links_off_hides_them_in_the_menus_and_closes_sent_links()
    {
        var policies = await AdminAsync();
        await policies.SaveAsync(new SharingPolicies(), default);
        var anna = await WebPage.OpenAsync(factory);
        await anna.Browser.CreateFolderAsync("Projekte", default);
        var share = new ShareViewModel(anna.Api, WebTexts.English, TimeProvider.System);
        var link = (await share.EnsureLinkAsync(anna.Browser.Space!, anna.Browser.Items.Single(), default))!;

        await policies.SaveAsync(new SharingPolicies(PublicLinks: false, FileRequests: false), default);
        await anna.Browser.RefreshPoliciesAsync(default);
        var visitor = new PublicLinkViewModel(
            new HttpDriveApi(factory.CreateDefaultClient(new Uri("http://localhost/"), new CookieContainerHandler())), WebTexts.English);
        await visitor.LoadAsync(link.Token, default);
        await share.OpenAsync(anna.Browser.Space!, anna.Browser.Items.Single(), default);
        await policies.SaveAsync(new SharingPolicies(), default);

        Assert.Equal((false, false), (anna.Browser.Policies.PublicLinks, anna.Browser.Policies.FileRequests));
        Assert.Equal(WebTexts.English.LinkNotFound, visitor.Problem);
        Assert.False(share.Policies.PublicLinks);
    }
}
