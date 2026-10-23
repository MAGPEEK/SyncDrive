using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>Ordnerbaum der Seitenleiste gegen den echten Server: nur Ordner, sortiert, Ebene für Ebene beim Aufklappen geladen.</summary>
public sealed class FolderTreeTests(HostFactory factory) : IClassFixture<HostFactory>
{
    [Fact]
    public async Task Expanding_loads_only_the_folders_of_that_level_sorted_and_collapsing_hides_them()
    {
        var page = await WebPage.OpenAsync(factory);
        var browser = page.Browser;
        Assert.True(await browser.CreateFolderAsync("projekte", default));
        Assert.True(await browser.CreateFolderAsync("Archiv", default));
        await browser.UploadAsync([WebPage.File("notiz.txt", "x")], default);
        await browser.OpenAsync(browser.Items.Single(i => i.Name == "projekte"), default);
        Assert.True(await browser.CreateFolderAsync("2026", default));
        var space = browser.Space!.Id;
        var tree = new FolderTreeViewModel(page.Api, WebTexts.English);

        Assert.Empty(tree.Children(space, string.Empty)); // zugeklappt
        await tree.ToggleAsync(space, string.Empty, default);
        Assert.Equal(["Archiv", "projekte"], tree.Children(space, string.Empty).Select(f => f.Name));

        await tree.ToggleAsync(space, "projekte", default);
        await tree.ToggleAsync(space, "Archiv", default);
        Assert.Equal(["2026"], tree.Children(space, "projekte").Select(f => f.Name));
        Assert.True(tree.IsLeaf(space, "Archiv")); // ohne Unterordner: kein Pfeil mehr
        Assert.False(tree.IsLeaf(space, "projekte"));

        await tree.ToggleAsync(space, string.Empty, default);
        Assert.Empty(tree.Children(space, string.Empty));
        Assert.False(tree.IsExpanded(space, string.Empty));
    }

    [Fact]
    public async Task A_refresh_reloads_open_levels_with_new_folders()
    {
        var page = await WebPage.OpenAsync(factory);
        var space = page.Browser.Space!.Id;
        var tree = new FolderTreeViewModel(page.Api, WebTexts.English);
        await tree.ToggleAsync(space, string.Empty, default);
        Assert.Empty(tree.Children(space, string.Empty));

        Assert.True(await page.Browser.CreateFolderAsync("Neu", default));
        await tree.RefreshAsync(default);

        Assert.Equal(["Neu"], tree.Children(space, string.Empty).Select(f => f.Name));
        Assert.True(tree.IsExpanded(space, string.Empty));
    }

    [Fact]
    public void Team_folders_start_expanded_and_toggle()
    {
        var tree = new FolderTreeViewModel(null!, WebTexts.English);

        Assert.True(tree.TeamFoldersExpanded);
        tree.ToggleTeamFolders();
        Assert.False(tree.TeamFoldersExpanded);
    }
}
