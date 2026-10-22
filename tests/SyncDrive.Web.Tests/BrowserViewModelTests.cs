using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using SyncDrive.Contracts;
using SyncDrive.Core.Chunking;
using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>
/// Die ViewModels der Weboberfläche gegen den echten Server-Host über den echten <see cref="HttpDriveApi"/> mit Sitzungs-Cookie, wie im Browser.
/// Jeder Test meldet einen eigenen Benutzer an, damit „My Drive“ leer beginnt.
/// </summary>
public sealed class BrowserViewModelTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = WebPage.Password;

    private Task<WebPage> OpenPageAsync(string? user = null) => WebPage.OpenAsync(factory, user);

    private static UploadFile File(string name, string content) => WebPage.File(name, content);

    private static UploadFile File(string name, byte[] content) => WebPage.File(name, content);

    [Fact]
    public async Task A_wrong_password_shows_the_message_and_keeps_the_form()
    {
        var api = new HttpDriveApi(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var session = new SessionViewModel(api, WebTexts.English);
        await session.CheckAsync(default);

        var ok = await session.SignInAsync(HostFactory.AdminName, "falsch", default);

        Assert.Equal((false, null, false, "Wrong username or password."), (ok, session.Session, session.IsChecking, session.Error));
    }

    [Fact]
    public async Task A_new_user_starts_in_an_empty_my_drive()
    {
        var page = await OpenPageAsync();

        Assert.Equal(page.User, page.Session.Session!.UserName);
        Assert.Equal(ListState.Empty, page.Browser.State);
        Assert.Equal([new Crumb("My Drive", string.Empty)], page.Browser.Breadcrumbs);
        Assert.True(page.Browser.CanWrite);
    }

    [Fact]
    public async Task Folders_are_created_opened_and_shown_in_the_breadcrumbs()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;

        Assert.True(await browser.CreateFolderAsync("Projekte", default));
        await browser.OpenAsync(browser.Items.Single(), default);
        Assert.True(await browser.CreateFolderAsync("2026", default));

        Assert.Equal(["My Drive", "Projekte"], browser.Breadcrumbs.Select(c => c.Label));
        Assert.Equal(["Projekte/2026"], browser.Items.Select(i => i.Path));
        Assert.Equal(["Projekte/2026"], browser.Selected); // das Neue ist gewählt
        await browser.NavigateAsync(browser.Breadcrumbs[0].Path, default);
        Assert.Equal(["Projekte"], browser.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Invalid_or_taken_names_give_a_message_and_change_nothing()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.CreateFolderAsync("Doppelt", default);

        var taken = await browser.CreateFolderAsync("Doppelt", default);
        var takenMessage = browser.Message;
        var invalid = await browser.CreateFolderAsync("a/b", default);

        Assert.Equal((false, "An item with this name already exists."), (taken, takenMessage));
        Assert.Equal((false, "This name is not allowed."), (invalid, browser.Message));
        Assert.Single(browser.Items);
    }

    [Fact]
    public async Task An_uploaded_file_is_listed_and_downloads_with_the_same_content()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        var content = Enumerable.Range(0, 5_000).Select(i => (byte)(i * 31)).ToArray();

        await browser.UploadAsync([File("daten.bin", content)], default);

        var item = Assert.Single(browser.Items);
        Assert.Equal(("daten.bin", 5_000L, false), (item.Name, item.Size, item.IsFolder));
        Assert.Empty(browser.Uploads);
        Assert.Equal(content, await page.Http.GetByteArrayAsync(browser.DownloadUrl(item)));
    }

    [Fact]
    public async Task Images_show_a_thumbnail_that_follows_the_revision_and_falls_back_to_the_icon()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        using var bitmap = new SkiaSharp.SKBitmap(300, 200);
        using var png = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        await browser.UploadAsync([File("foto.png", png.ToArray()), File("kaputt.jpg", [1, 2, 3]), File("notiz.txt", [65])], default);
        await browser.CreateFolderAsync("Ordner", default);
        var (photo, broken) = (browser.Items.Single(i => i.Name == "foto.png"), browser.Items.Single(i => i.Name == "kaputt.jpg"));

        var url = browser.ThumbnailUrl(photo)!;
        using var thumbnail = await page.Http.GetAsync(url);
        var brokenUrl = browser.ThumbnailUrl(broken)!;
        using var noPreview = await page.Http.GetAsync(brokenUrl);
        browser.ThumbnailFailed(brokenUrl);

        Assert.Contains($"v={photo.Revision}", url, StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NotFound), (thumbnail.StatusCode, noPreview.StatusCode));
        Assert.Null(browser.ThumbnailUrl(broken));
        Assert.Null(browser.ThumbnailUrl(browser.Items.Single(i => i.Name == "notiz.txt")));
        Assert.Null(browser.ThumbnailUrl(browser.Items.Single(i => i.IsFolder)));
    }

    [Fact]
    public async Task Uploading_again_sends_only_changed_blocks_and_a_change_becomes_a_new_version()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        var content = new byte[20_000];
        new Random(5).NextBytes(content);
        await browser.UploadAsync([File("a.bin", content)], default);
        var first = browser.Items.Single();
        var allBlocks = page.Requests.Count(IsBlockUpload);

        page.Requests.Clear();
        await browser.UploadAsync([File("a.bin", content)], default);
        var sameAgain = (page.Requests.Count(IsBlockUpload), browser.Items.Single().Revision);

        var changed = (byte[])content.Clone();
        changed[10_000] ^= 0xFF; // ein Byte in der Mitte
        page.Requests.Clear();
        await browser.UploadAsync([File("a.bin", changed)], default);
        var changedBlocks = page.Requests.Count(IsBlockUpload);

        Assert.Equal((0, first.Revision), sameAgain); // gleicher Inhalt: nichts übertragen, keine neue Version
        Assert.InRange(changedBlocks, 1, allBlocks / 2);
        Assert.True(browser.Items.Single().Revision > first.Revision);
        Assert.Equal(changed, await page.Http.GetByteArrayAsync(browser.DownloadUrl(browser.Items.Single())));
        Assert.Null(browser.Message);

        static bool IsBlockUpload(string request) => request.StartsWith("PUT /api/v1/blocks/", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renaming_and_deleting_work_on_the_selection()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.UploadAsync([File("alt.txt", "x"), File("weg1.txt", "y"), File("weg2.txt", "z")], default);

        Assert.True(await browser.RenameAsync(browser.Items.Single(i => i.Name == "alt.txt"), "neu.txt", default));
        browser.Select(browser.Items.Single(i => i.Name == "weg1.txt"));
        browser.Select(browser.Items.Single(i => i.Name == "weg2.txt"), toggle: true);
        var question = browser.DeleteQuestion;
        await browser.DeleteSelectedAsync(default);

        Assert.Equal("Move 2 item(s) to the Recycle Bin?", question);
        Assert.Equal(["neu.txt"], browser.Items.Select(i => i.Name));
        Assert.Empty(browser.Selected);
    }

    [Fact]
    public async Task Selection_works_like_the_explorer_with_ctrl_and_shift()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.UploadAsync([File("a.txt", "1"), File("b.txt", "2"), File("c.txt", "3"), File("d.txt", "4")], default);
        var items = browser.Items;

        browser.Select(items[0]);
        browser.Select(items[2], range: true);
        var range = browser.Selected.Order().ToList();
        browser.Select(items[1], toggle: true);
        browser.SelectForContextMenu(items[0]); // schon gewählt: Auswahl bleibt
        var afterContext = browser.Selected.Order().ToList();
        browser.SelectForContextMenu(items[3]); // nicht gewählt: nur er

        Assert.Equal(["a.txt", "b.txt", "c.txt"], range);
        Assert.Equal(["a.txt", "c.txt"], afterContext);
        Assert.Equal(["d.txt"], browser.Selected);
    }

    [Fact]
    public async Task Folders_come_first_and_columns_sort_both_ways()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.UploadAsync([File("klein.txt", "1"), File("gross.txt", new string('x', 2_000))], default);
        await browser.CreateFolderAsync("z-Ordner", default);

        var byName = browser.Items.Select(i => i.Name).ToList();
        browser.SortBy(SortColumn.Size);
        browser.SortBy(SortColumn.Size); // zweiter Klick: absteigend
        var bySizeDescending = browser.Items.Select(i => i.Name).ToList();

        Assert.Equal(["z-Ordner", "gross.txt", "klein.txt"], byName);
        Assert.Equal(["z-Ordner", "gross.txt", "klein.txt"], bySizeDescending);
        Assert.True(browser.Descending);
    }

    [Fact]
    public async Task Changes_from_another_device_appear_without_reloading()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        using var stop = new CancellationTokenSource();
        var live = browser.RunLiveUpdatesAsync(stop.Token);
        var other = await factory.LoginAsync(page.User, Password);

        await Task.Delay(300); // der Long-Poll wartet schon
        var created = await other.Http.PostAsJsonAsync(
            $"/api/v1/spaces/{browser.Space!.Id}/commit", new CommitRequest(CommitOps.Mkdir, "vom Laptop", 0));
        Assert.True(created.IsSuccessStatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (browser.Items.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await stop.CancelAsync();
        await live.ContinueWith(_ => { }, TaskScheduler.Default);
        Assert.Equal(["vom Laptop"], browser.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_viewer_sees_a_shared_space_but_cannot_write()
    {
        var owner = await OpenPageAsync();
        await owner.Browser.CreateFolderAsync("Geteilt", default);
        var viewerName = await factory.CreateUserAsync("viewer-" + Guid.NewGuid().ToString("N")[..8]);
        var ownerApi = await factory.LoginAsync(owner.User, Password);
        var add = await ownerApi.Http.PostAsJsonAsync(
            $"/api/v1/spaces/{owner.Browser.Space!.Id}/members", new AddMemberRequest(viewerName, "viewer"));
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);

        var viewer = await OpenPageAsync(viewerName);
        var shared = viewer.Browser.Spaces.Single(s => s.Id == owner.Browser.Space.Id);
        await viewer.Browser.OpenSpaceAsync(shared, default);
        await viewer.Browser.UploadAsync([File("nein.txt", "x")], default);

        Assert.False(viewer.Browser.CanWrite);
        Assert.Equal("You do not have permission for this action.", viewer.Browser.Message);
        Assert.Equal(["Geteilt"], viewer.Browser.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Signing_out_revokes_the_session_and_the_list_reports_it()
    {
        var page = await OpenPageAsync();
        var signedOut = false;
        page.Browser.SignedOut += () => signedOut = true;

        await page.Session.SignOutAsync(default);
        await page.Browser.LoadAsync(default);

        Assert.Null(page.Session.Session);
        Assert.True(signedOut);
        Assert.Equal(HttpStatusCode.Unauthorized, (await page.Http.GetAsync("api/v1/web/session")).StatusCode);
    }
}
