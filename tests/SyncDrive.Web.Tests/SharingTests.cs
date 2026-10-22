using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using SyncDrive.Contracts;
using SyncDrive.Core.Chunking;
using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>M6b gegen den echten Host: Freigabedialog, „Get link“, „Shared with others“, Seite eines öffentlichen Links, Dateianfrage.</summary>
public sealed class SharingTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private static readonly ChunkerOptions SmallChunks = new(MinSize: 64, AverageSize: 256, MaxSize: 1024);

    private static ShareViewModel Share(WebPage page) => new(page.Api, WebTexts.English, TimeProvider.System);

    /// <summary>Ein anonymer Browser für <c>/s/{token}</c>.</summary>
    private PublicLinkViewModel Visitor() => new(
        new HttpDriveApi(factory.CreateDefaultClient(new Uri("http://localhost/"), new CookieContainerHandler()), SmallChunks), WebTexts.English);

    private static async Task<DriveItem> FolderWithFilesAsync(WebPage page)
    {
        var browser = page.Browser;
        await browser.CreateFolderAsync("Projekte", default);
        await browser.OpenAsync(browser.Items.Single(), default);
        await browser.CreateFolderAsync("Entwürfe", default);
        await browser.UploadAsync([WebPage.File("plan.txt", "Plan")], default);
        await browser.NavigateAsync(string.Empty, default);
        return browser.Items.Single();
    }

    [Fact]
    public async Task The_share_dialog_invites_changes_the_role_and_removes_people()
    {
        var anna = await WebPage.OpenAsync(factory);
        var ben = await WebPage.OpenAsync(factory);
        var folder = await FolderWithFilesAsync(anna);
        var share = Share(anna);
        await share.OpenAsync(anna.Browser.Space!, folder, default);

        var unknown = await share.InviteAsync("niemand-" + Guid.NewGuid().ToString("N")[..6], ShareViewModel.DefaultRole, default);
        var unknownMessage = share.Message;
        var invited = await share.InviteAsync(ben.User, ShareViewModel.DefaultRole, default);
        var asViewer = share.Invitees.Single();
        await share.ChangeRoleAsync(asViewer, "editor", default);
        var asEditor = share.Invitees.Single();
        var twice = await share.InviteAsync(anna.User, "viewer", default);
        var twiceMessage = share.Message;
        await ben.Browser.ReloadSpacesAsync(default);
        var benSees = ben.Browser.Spaces.Single(s => s.SharedBy is not null);
        await share.RemoveAsync(asEditor, default);

        Assert.Equal((false, WebTexts.English.UnknownUser), (unknown, unknownMessage));
        Assert.True(invited);
        Assert.Equal((ben.User, "viewer"), (asViewer.UserName, asViewer.Role));
        Assert.Equal("editor", asEditor.Role);
        Assert.Equal((false, WebTexts.English.AlreadyHasAccess), (twice, twiceMessage));
        Assert.Equal(("Projekte", anna.User, "editor"), (benSees.Name, benSees.SharedBy, benSees.Role));
        Assert.Empty(share.Invitees);
        Assert.Equal("Share “Projekte”", share.Title);
    }

    [Fact]
    public async Task An_invited_user_browses_and_uploads_in_the_shared_folder_like_in_a_space()
    {
        var anna = await WebPage.OpenAsync(factory);
        var ben = await WebPage.OpenAsync(factory);
        var folder = await FolderWithFilesAsync(anna);
        var share = Share(anna);
        await share.OpenAsync(anna.Browser.Space!, folder, default);
        await share.InviteAsync(ben.User, "editor", default);

        await ben.Browser.ReloadSpacesAsync(default);
        await ben.Browser.OpenSpaceAsync(ben.Browser.Spaces.Single(s => s.SharedBy is not null), default);
        var items = ben.Browser.Items.Select(i => i.Name).ToList();
        var crumbs = ben.Browser.Breadcrumbs.Select(c => c.Label).ToList();
        await ben.Browser.UploadAsync([WebPage.File("von-ben.txt", "Hallo")], default);
        await anna.Browser.OpenAsync(folder, default);

        Assert.Equal(["Entwürfe", "plan.txt"], items);
        Assert.Equal(["Projekte"], crumbs);
        Assert.Contains("von-ben.txt", anna.Browser.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_public_link_with_password_and_expiry_is_saved_kept_and_removed()
    {
        var anna = await WebPage.OpenAsync(factory);
        var folder = await FolderWithFilesAsync(anna);
        var share = Share(anna);
        await share.OpenAsync(anna.Browser.Space!, folder, default);
        var editor = share.ShareLink;
        var tomorrow = DateTime.Now.Date.AddDays(1);

        editor.Enabled = true;
        editor.UsePassword = true;
        var missing = await share.SaveLinkAsync(editor, default);
        var missingMessage = share.Message;
        editor.Password = "sesam123";
        editor.UseExpiry = true;
        editor.ExpiryDate = tomorrow;
        await share.SaveLinkAsync(editor, default);
        var saved = editor.Link!;
        await share.SaveLinkAsync(editor, default); // leeres Passwortfeld: Passwort bleibt
        var kept = editor.Link!;
        editor.Enabled = false;
        await share.SaveLinkAsync(editor, default);

        Assert.Equal((false, WebTexts.English.PasswordMissing), (missing, missingMessage));
        Assert.True(saved.HasPassword);
        Assert.Equal(tomorrow, saved.ExpiresAt!.Value.ToLocalTime().Date);
        Assert.Equal((saved.Token, true), (kept.Token, kept.HasPassword));
        Assert.Null(editor.Link);
        Assert.False(editor.Enabled);
        Assert.Equal("https://drive.example/s/" + saved.Token, ShareViewModel.UrlOf(saved, "https://drive.example/"));
    }

    [Fact]
    public async Task Get_link_reuses_the_existing_link()
    {
        var anna = await WebPage.OpenAsync(factory);
        await anna.Browser.UploadAsync([WebPage.File("a.txt", "a")], default);
        var file = anna.Browser.Items.Single();
        var share = Share(anna);

        var first = await share.EnsureLinkAsync(anna.Browser.Space!, file, default);
        var second = await share.EnsureLinkAsync(anna.Browser.Space!, file, default);

        Assert.NotNull(first);
        Assert.Equal(first.Token, second!.Token);
    }

    [Fact]
    public async Task Shared_with_others_and_file_requests_list_what_the_user_shared()
    {
        var anna = await WebPage.OpenAsync(factory);
        var ben = await WebPage.OpenAsync(factory);
        var folder = await FolderWithFilesAsync(anna);
        var share = Share(anna);
        await share.OpenAsync(anna.Browser.Space!, folder, default);
        await share.InviteAsync(ben.User, "viewer", default);
        share.RequestLink.Enabled = true;
        await share.SaveLinkAsync(share.RequestLink, default);
        var shared = new SharedByMeViewModel(anna.Api, WebTexts.English, TimeProvider.System);

        await shared.LoadAsync(anna.Browser.Spaces, default);
        var row = Assert.Single(shared.Rows);

        Assert.Equal("1 people · file request", shared.Summary(row));
        Assert.Equal([row], shared.Requests);
        Assert.Equal("Active", shared.StatusOf(row.Shared.Request!));
        Assert.Equal(("Projekte", true), (row.Located.Item.Name, row.Located.Item.IsFolder));
    }

    [Fact]
    public async Task A_visitor_browses_a_folder_link_and_downloads_without_an_account()
    {
        var anna = await WebPage.OpenAsync(factory);
        var folder = await FolderWithFilesAsync(anna);
        var link = (await Share(anna).EnsureLinkAsync(anna.Browser.Space!, folder, default))!;
        var visitor = Visitor();

        await visitor.LoadAsync(link.Token, default);
        var top = visitor.Items.Select(i => i.Name).ToList();
        await visitor.NavigateAsync("Entwürfe", default);
        var crumbs = visitor.Breadcrumbs.Select(c => c.Label).ToList();
        await visitor.NavigateAsync(string.Empty, default);
        using var http = factory.CreateDefaultClient(new Uri("http://localhost/"));
        var content = await http.GetStringAsync(visitor.DownloadUrl(visitor.Items.Single(i => i.Name == "plan.txt")));

        Assert.Equal((false, true, false), (visitor.NeedsPassword, visitor.IsFolder, visitor.IsRequest));
        Assert.Equal(["Entwürfe", "plan.txt"], top);
        Assert.Equal(["Projekte", "Entwürfe"], crumbs);
        Assert.Equal("Plan", content);
        Assert.Equal(("Projekte.zip", $"api/v1/public/{link.Token}/zip"), (visitor.ZipName(), visitor.ZipUrl()));
        Assert.Equal(anna.User, visitor.Info!.Owner);
    }

    [Fact]
    public async Task A_visitor_needs_the_password_first()
    {
        var anna = await WebPage.OpenAsync(factory);
        var folder = await FolderWithFilesAsync(anna);
        var share = Share(anna);
        await share.OpenAsync(anna.Browser.Space!, folder, default);
        share.ShareLink.Enabled = true;
        share.ShareLink.UsePassword = true;
        share.ShareLink.Password = "sesam123";
        await share.SaveLinkAsync(share.ShareLink, default);
        var visitor = Visitor();

        await visitor.LoadAsync(share.ShareLink.Link!.Token, default);
        var needed = visitor.NeedsPassword;
        var lockedItems = visitor.Items.Count;
        var wrong = await visitor.UnlockAsync("falsch", default);
        var wrongMessage = visitor.Message;
        var right = await visitor.UnlockAsync("sesam123", default);

        Assert.Equal((true, 0), (needed, lockedItems));
        Assert.Equal((false, WebTexts.English.WrongPassword), (wrong, wrongMessage));
        Assert.True(right);
        Assert.False(visitor.NeedsPassword);
        Assert.Equal(["Entwürfe", "plan.txt"], visitor.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_visitor_uploads_to_a_file_request_in_blocks_and_nothing_is_overwritten()
    {
        var anna = await WebPage.OpenAsync(factory);
        var folder = await FolderWithFilesAsync(anna);
        var share = Share(anna);
        await share.OpenAsync(anna.Browser.Space!, folder, default);
        share.RequestLink.Enabled = true;
        await share.SaveLinkAsync(share.RequestLink, default);
        var visitor = Visitor();
        var big = new byte[5000];
        new Random(7).NextBytes(big);

        await visitor.LoadAsync(share.RequestLink.Link!.Token, default);
        await visitor.UploadAsync([WebPage.File("plan.txt", "von außen"), WebPage.File("groß.bin", big)], default);
        await anna.Browser.OpenAsync(folder, default);
        using var http = factory.CreateDefaultClient(new Uri("http://localhost/"));

        Assert.True(visitor.IsRequest);
        Assert.Empty(visitor.Items); // eine Dateianfrage zeigt keinen Inhalt
        Assert.Equal($"{anna.User} asks you to upload files to “Projekte”.", visitor.RequestHint);
        Assert.Equal(["plan (2).txt", "groß.bin"], visitor.Uploaded);
        Assert.Equal(["Entwürfe", "groß.bin", "plan (2).txt", "plan.txt"], anna.Browser.Items.Select(i => i.Name));
        Assert.Equal(big, await anna.Http.GetByteArrayAsync(anna.Browser.DownloadUrl(anna.Browser.Items.Single(i => i.Name == "groß.bin"))));
        Assert.Empty(visitor.Uploads);
    }

    [Fact]
    public async Task An_unknown_link_shows_a_plain_message()
    {
        var visitor = Visitor();

        await visitor.LoadAsync("gibt-es-nicht", default);

        Assert.Equal(WebTexts.English.LinkNotFound, visitor.Problem);
        Assert.False(visitor.IsLoading);
    }
}
