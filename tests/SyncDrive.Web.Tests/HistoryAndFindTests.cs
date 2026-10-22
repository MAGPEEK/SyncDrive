using System.Net;
using System.Net.Http.Json;
using SyncDrive.Contracts;
using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>M6.2 gegen den echten Host: Papierkorb, Versionen, Suche mit Bereich, „Zuletzt“, „Speicherort öffnen“.</summary>
public sealed class HistoryAndFindTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private Task<WebPage> OpenPageAsync(string? user = null) => WebPage.OpenAsync(factory, user);

    [Fact]
    public async Task Deleted_items_appear_in_the_recycle_bin_and_come_back_to_their_place()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.CreateFolderAsync("Projekte", default);
        await browser.OpenAsync(browser.Items.Single(), default);
        await browser.UploadAsync([WebPage.File("plan.txt", "Plan"), WebPage.File("bleibt.txt", "x")], default);
        browser.Select(browser.Items.Single(i => i.Name == "plan.txt"));
        await browser.DeleteSelectedAsync(default);
        var trash = new TrashViewModel(page.Api, WebTexts.English);

        await trash.LoadAsync(browser.Spaces, default);
        var row = Assert.Single(trash.Rows);
        trash.Select(row);
        await trash.RestoreSelectedAsync(browser.Spaces, default);
        await browser.LoadAsync(default);

        Assert.Equal(("plan.txt", "My Drive / Projekte"), (row.Entry.Name, row.Location(browser.SpaceLabel)));
        Assert.Equal(ListState.Empty, trash.State);
        Assert.Equal("Restored 1 item(s) to their original location.", trash.Message);
        Assert.Equal(["bleibt.txt", "plan.txt"], browser.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_deleted_folder_is_one_entry_and_returns_with_its_content()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.CreateFolderAsync("Alt", default);
        await browser.OpenAsync(browser.Items.Single(), default);
        await browser.UploadAsync([WebPage.File("a.txt", "1"), WebPage.File("b.txt", "2")], default);
        await browser.NavigateAsync(string.Empty, default);
        browser.Select(browser.Items.Single());
        await browser.DeleteSelectedAsync(default);
        var trash = new TrashViewModel(page.Api, WebTexts.English);

        await trash.LoadAsync(browser.Spaces, default);
        var row = Assert.Single(trash.Rows);
        trash.Select(row);
        await trash.RestoreSelectedAsync(browser.Spaces, default);
        await browser.RevealAsync(browser.Space!, "Alt/a.txt", default);

        Assert.True(row.IsFolder);
        Assert.Equal(["a.txt", "b.txt"], browser.Items.Select(i => i.Name));
        Assert.Equal(["Alt/a.txt"], browser.Selected);
    }

    [Fact]
    public async Task A_viewer_sees_the_shared_recycle_bin_but_cannot_restore()
    {
        var owner = await OpenPageAsync();
        await owner.Browser.UploadAsync([WebPage.File("weg.txt", "x")], default);
        owner.Browser.Select(owner.Browser.Items.Single());
        await owner.Browser.DeleteSelectedAsync(default);
        var viewerName = await factory.CreateUserAsync("viewer-" + Guid.NewGuid().ToString("N")[..8]);
        await ShareAsync(owner, viewerName, "viewer");
        var viewer = await OpenPageAsync(viewerName);
        var trash = new TrashViewModel(viewer.Api, WebTexts.English);

        await trash.LoadAsync(viewer.Browser.Spaces, default);
        trash.Select(trash.Rows.Single(r => r.Space.Id == owner.Browser.Space!.Id));

        Assert.False(trash.CanRestore);
    }

    [Fact]
    public async Task Versions_list_newest_first_download_old_content_and_restore_it()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.UploadAsync([WebPage.File("text.txt", "erste Fassung")], default);
        await browser.UploadAsync([WebPage.File("text.txt", "zweite Fassung")], default);
        var file = browser.Items.Single();
        var versions = new VersionsViewModel(page.Api, WebTexts.English);

        await versions.OpenAsync(browser.Space!, file, default);
        var listed = versions.Versions;
        var oldest = listed[^1];
        var oldContent = await page.Http.GetStringAsync(versions.DownloadUrl(oldest));
        var restored = await versions.RestoreAsync(oldest, default);
        await browser.LoadAsync(default);

        Assert.Equal("Versions of text.txt", versions.Title);
        Assert.Equal(2, listed.Count);
        Assert.True(listed[0].Revision > listed[1].Revision);
        Assert.Equal("erste Fassung", oldContent);
        Assert.True(restored);
        Assert.Equal(3, versions.Versions.Count);
        Assert.True(versions.IsCurrent(versions.Versions[0]));
        Assert.Equal("The selected version is now the current one.", versions.Message);
        Assert.Equal("erste Fassung", await page.Http.GetStringAsync(browser.DownloadUrl(browser.Items.Single())));
    }

    [Fact]
    public async Task Search_covers_the_current_folder_or_everything_and_opens_the_location()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.CreateFolderAsync("Berichte", default);
        await browser.CreateFolderAsync("Sonstiges", default);
        await browser.UploadAsync([WebPage.File("Bericht oben.txt", "1")], default);
        await browser.OpenAsync(browser.Items.Single(i => i.Name == "Berichte"), default);
        await browser.UploadAsync([WebPage.File("Jahresbericht.txt", "2")], default);
        var search = new SearchViewModel(page.Api, WebTexts.English);

        await search.SearchAsync("bericht", everywhere: false, browser.Spaces, browser.Space, browser.Path, default);
        var inFolder = search.Results.Select(r => r.Item.Path).ToList();
        await search.SearchAsync("BERICHT", everywhere: true, browser.Spaces, browser.Space, browser.Path, default);
        var everywhere = search.Results.Select(r => r.Item.Path).ToList();
        await search.SearchAsync("gibtsnicht", everywhere: true, browser.Spaces, browser.Space, browser.Path, default);
        var none = search.State;
        var hit = (await SearchAgainAsync(search, browser)).Results.Single(r => r.Item.Name == "Bericht oben.txt");
        await browser.RevealAsync(hit.Space, hit.Item.Path, default);

        Assert.Equal(["Berichte/Jahresbericht.txt"], inFolder);
        Assert.Equal(["Bericht oben.txt", "Berichte", "Berichte/Jahresbericht.txt"], everywhere.Order(StringComparer.Ordinal));
        Assert.Equal(ListState.Empty, none);
        Assert.Equal(string.Empty, browser.Path);
        Assert.Equal(["Bericht oben.txt"], browser.Selected);
    }

    [Fact]
    public async Task Recent_shows_files_of_all_spaces_newest_first()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        var now = DateTimeOffset.UtcNow;
        await browser.UploadAsync([WebPage.File("alt.txt", "1"u8.ToArray(), now.AddDays(-3)), WebPage.File("neu.txt", "2"u8.ToArray(), now)], default);
        await browser.CreateFolderAsync("Ordner", default);
        var recent = new RecentViewModel(page.Api, WebTexts.English);

        await recent.LoadAsync(browser.Spaces, default);

        Assert.Equal(["neu.txt", "alt.txt"], recent.Items.Select(i => i.Item.Name));
        Assert.Equal("My Drive", recent.Items[0].Location(browser.SpaceLabel));
    }

    private static async Task<SearchViewModel> SearchAgainAsync(SearchViewModel search, BrowserViewModel browser)
    {
        await search.SearchAsync("bericht", everywhere: true, browser.Spaces, browser.Space, browser.Path, default);
        return search;
    }

    private async Task ShareAsync(WebPage owner, string userName, string role)
    {
        var ownerApi = await factory.LoginAsync(owner.User, WebPage.Password);
        var add = await ownerApi.Http.PostAsJsonAsync(
            $"/api/v1/spaces/{owner.Browser.Space!.Id}/members", new AddMemberRequest(userName, role));
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
    }
}
