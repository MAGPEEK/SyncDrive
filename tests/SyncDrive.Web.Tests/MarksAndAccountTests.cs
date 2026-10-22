using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using SyncDrive.Contracts;
using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>M6.3 gegen den echten Host: Favoriten, Labels, Benachrichtigungen, Geräteliste mit „Unlink“, Speicher, ZIP-Download.</summary>
public sealed class MarksAndAccountTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private Task<WebPage> OpenPageAsync(string? user = null) => WebPage.OpenAsync(factory, user);

    [Fact]
    public async Task A_star_follows_the_item_through_a_rename_and_lists_it_under_starred()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.CreateFolderAsync("Projekte", default);
        await browser.OpenAsync(browser.Items.Single(), default);
        await browser.UploadAsync([WebPage.File("plan.txt", "Plan"), WebPage.File("notiz.txt", "x")], default);
        var marks = new MarksViewModel(page.Api, WebTexts.English);
        await marks.LoadAsync(default);
        var plan = browser.Items.Single(i => i.Name == "plan.txt");

        await marks.ToggleStarAsync([new LocatedItem(browser.Space!, plan)], default);
        await browser.RenameAsync(plan, "plan-v2.txt", default);
        await marks.LoadAsync(default);
        var renamed = browser.Items.Single(i => i.Name == "plan-v2.txt");
        var starred = Assert.Single(marks.Starred(browser.Spaces));

        Assert.True(marks.IsStarred(browser.Space!, renamed));
        Assert.False(marks.IsStarred(browser.Space!, browser.Items.Single(i => i.Name == "notiz.txt")));
        Assert.Equal(("Projekte/plan-v2.txt", 4L, "My Drive / Projekte"), (starred.Item.Path, starred.Item.Size, starred.Location(browser.SpaceLabel)));

        await marks.ToggleStarAsync([new LocatedItem(browser.Space!, renamed)], default);
        Assert.Empty(marks.Starred(browser.Spaces));
    }

    [Fact]
    public async Task Starring_a_selection_stars_all_unless_all_are_starred_already()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.UploadAsync([WebPage.File("a.txt", "1"), WebPage.File("b.txt", "2")], default);
        var marks = new MarksViewModel(page.Api, WebTexts.English);
        await marks.LoadAsync(default);
        browser.Select(browser.Items[0]);
        await marks.ToggleStarAsync(browser.SelectedLocated, default);
        browser.SelectAll();

        var someStarred = marks.AllStarred(browser.SelectedLocated);
        await marks.ToggleStarAsync(browser.SelectedLocated, default);
        var afterFirst = marks.Starred(browser.Spaces).Count;
        await marks.ToggleStarAsync(browser.SelectedLocated, default);

        Assert.Equal((false, 2), (someStarred, afterFirst));
        Assert.Empty(marks.Starred(browser.Spaces));
    }

    [Fact]
    public async Task A_new_label_from_the_menu_goes_to_the_selection_and_toggles_like_the_star()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.CreateFolderAsync("Belege", default);
        await browser.UploadAsync([WebPage.File("rechnung.pdf", "R")], default);
        var marks = new MarksViewModel(page.Api, WebTexts.English);
        await marks.LoadAsync(default);
        browser.Select(browser.Items.Single(i => i.Name == "rechnung.pdf"));

        Assert.True(await marks.CreateLabelAsync(" Steuer ", "#3c9d5d", browser.SelectedLocated, default));
        var label = Assert.Single(marks.Labels);
        var invoice = browser.Items.Single(i => i.Name == "rechnung.pdf");
        Assert.Equal(("Steuer", "#3c9d5d"), (label.Name, label.Color));
        Assert.Equal([label], marks.LabelsOf(browser.Space!, invoice));

        browser.SelectAll();
        await marks.ToggleLabelAsync(label, browser.SelectedLocated, default); // der Ordner hatte es noch nicht: beide bekommen es
        Assert.Equal(["Belege", "rechnung.pdf"], marks.ItemsOf(label, browser.Spaces).Select(i => i.Item.Name));
        await marks.ToggleLabelAsync(label, browser.SelectedLocated, default);
        Assert.Empty(marks.ItemsOf(label, browser.Spaces));
    }

    [Fact]
    public async Task Label_names_must_be_present_short_and_unique_and_deleting_keeps_the_files()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.UploadAsync([WebPage.File("a.txt", "1")], default);
        var marks = new MarksViewModel(page.Api, WebTexts.English);
        await marks.LoadAsync(default);
        await marks.CreateLabelAsync("Privat", MarksViewModel.Colors[0], browser.SelectedLocated, default);

        var empty = await marks.CreateLabelAsync("  ", MarksViewModel.Colors[0], [], default);
        var emptyMessage = marks.Message;
        var tooLong = await marks.CreateLabelAsync(new string('x', 65), MarksViewModel.Colors[0], [], default);
        var duplicate = await marks.CreateLabelAsync("privat", MarksViewModel.Colors[1], [], default);
        var duplicateMessage = marks.Message;
        browser.SelectAll();
        await marks.ToggleLabelAsync(marks.Labels[0], browser.SelectedLocated, default);
        var deleted = await marks.DeleteLabelAsync(marks.Labels[0], default);
        await browser.LoadAsync(default);

        Assert.Equal((false, false, false), (empty, tooLong, duplicate));
        Assert.Equal(WebTexts.English.InvalidLabelName, emptyMessage);
        Assert.Equal(WebTexts.English.LabelExists, duplicateMessage);
        Assert.True(deleted);
        Assert.Empty(marks.Labels);
        Assert.Equal(["a.txt"], browser.Items.Select(i => i.Name));
        Assert.Equal("Delete the label “Privat”? The files and folders stay where they are; only the label is removed.",
            marks.DeleteLabelQuestion(new LabelInfo("l", "Privat", "#000000")));
    }

    [Fact]
    public async Task Sharing_a_space_notifies_the_new_member_until_it_is_read()
    {
        var owner = await OpenPageAsync();
        var member = await OpenPageAsync();
        var space = owner.Browser.Space!;
        using (var add = await owner.Http.PostAsJsonAsync($"api/v1/spaces/{space.Id}/members", new AddMemberRequest(member.User, "editor")))
        {
            add.EnsureSuccessStatusCode();
        }

        var notes = new NotificationsViewModel(member.Api, WebTexts.English);
        await member.Browser.ReloadSpacesAsync(default);
        await notes.LoadAsync(default);
        var note = Assert.Single(notes.Items);
        var text = notes.TextOf(note, id => member.Browser.Spaces.Single(s => s.Id == id).Name);
        var unreadBefore = notes.Unread;
        await notes.MarkReadAsync(note, default);
        await notes.LoadAsync(default);

        Assert.Equal((NotificationsViewModel.Shared, 1), (note.Type, unreadBefore));
        Assert.Equal($"{owner.User} shared “{space.Name}” with you", text);
        Assert.Contains(member.Browser.Spaces, s => s.Id == space.Id);
        Assert.Equal(0, notes.Unread);
        Assert.True(notes.Items.Single().Read);
    }

    [Fact]
    public async Task Polling_picks_up_new_notifications_and_stops_with_the_page()
    {
        var owner = await OpenPageAsync();
        var member = await OpenPageAsync();
        var notes = new NotificationsViewModel(member.Api, WebTexts.English);
        using var stop = new CancellationTokenSource();
        var arrived = new TaskCompletionSource();
        notes.Changed += () =>
        {
            if (notes.Unread > 0)
            {
                arrived.TrySetResult();
            }
        };
        var polling = notes.RunPollingAsync(TimeSpan.FromMilliseconds(50), stop.Token);

        using (var add = await owner.Http.PostAsJsonAsync(
            $"api/v1/spaces/{owner.Browser.Space!.Id}/members", new AddMemberRequest(member.User, "viewer")))
        {
            add.EnsureSuccessStatusCode();
        }

        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await polling.WaitAsync(TimeSpan.FromSeconds(10)); // endet still, ohne Ausnahme

        Assert.Equal(1, notes.Unread);
        Assert.Null(notes.Message);
    }

    [Fact]
    public async Task The_client_list_shows_all_devices_and_unlinks_another_one_but_not_this_browser()
    {
        var page = await OpenPageAsync();
        var other = await OpenPageAsync(page.User); // zweiter Browser desselben Benutzers
        var account = new AccountViewModel(page.Api, WebTexts.English);

        await account.LoadDevicesAsync(default);
        var listed = account.Devices.Count;
        var current = account.Devices.Single(d => d.IsCurrent);
        account.SelectDevice(current);
        var canUnlinkSelf = account.CanUnlink;
        var refusedSelf = await account.UnlinkSelectedAsync(default);
        account.SelectDevice(account.Devices.Single(d => !d.IsCurrent));
        var question = account.UnlinkQuestion;
        var unlinked = await account.UnlinkSelectedAsync(default);

        Assert.Equal((2, false, false, true), (listed, canUnlinkSelf, refusedSelf, unlinked));
        Assert.Equal("Unlink “Browser (Web)”? The device has to sign in again before it can sync.", question);
        Assert.Equal("“Browser (Web)” was unlinked.", account.Message);
        Assert.Equal([current.DeviceId], account.Devices.Select(d => d.DeviceId));
        Assert.Equal(("Web browser", "Desktop client"), (account.ClientTypeOf(current), account.ClientTypeOf(current with { ClientType = "desktop" })));
        Assert.Null(await other.Api.GetSessionAsync(default)); // der getrennte Browser ist abgemeldet
        Assert.NotNull(await page.Api.GetSessionAsync(default));
    }

    [Fact]
    public async Task The_account_menu_shows_the_storage_used_by_own_spaces_and_the_quota_once_one_is_set()
    {
        var page = await OpenPageAsync();
        await page.Browser.UploadAsync([WebPage.File("a.txt", "1234"), WebPage.File("b.txt", "567")], default);
        var account = new AccountViewModel(page.Api, WebTexts.English);

        await account.LoadStorageAsync(default);
        var unlimited = (account.UsedBytes, account.UsedText, account.QuotaShare);
        var adminPage = await WebPage.OpenAsync(factory, HostFactory.AdminName, HostFactory.AdminPassword);
        var admin = new AdminViewModel(adminPage.Api, WebTexts.English) { Me = HostFactory.AdminName };
        await admin.LoadAsync(default);
        await admin.SaveSettingsAsync(admin.Users.Single(u => u.Name == page.User), isAdmin: false, quotaGb: 1, default);
        var quota = admin.Users.Single(u => u.Name == page.User).QuotaBytes!.Value;
        await account.LoadStorageAsync(default);

        Assert.Equal(((long?)7, "7 B used", (double?)null), unlimited);
        Assert.Equal($"7 B of {WebTexts.FormatSize(quota)} used", account.UsedText);
        Assert.InRange(account.QuotaShare!.Value, double.Epsilon, 0.001);
    }

    [Fact]
    public async Task A_folder_downloads_as_a_zip_with_its_whole_tree()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        var big = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"Zeile {i}\n"))); // viele Blöcke
        await browser.CreateFolderAsync("Projekte", default);
        await browser.OpenAsync(browser.Items.Single(), default);
        await browser.CreateFolderAsync("Leer", default);
        await browser.UploadAsync([WebPage.File("groß.txt", big), WebPage.File("klein.txt", "k")], default);
        await browser.NavigateAsync(string.Empty, default);
        browser.Select(browser.Items.Single());

        var url = browser.SelectionDownloadUrl!;
        using var response = await page.Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());

        Assert.Equal(("Projekte.zip", "Projekte.zip"), (browser.SelectionDownloadName, response.Content.Headers.ContentDisposition!.FileNameStar));
        Assert.Equal(["Projekte/", "Projekte/Leer/", "Projekte/groß.txt", "Projekte/klein.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(big, Read(zip.GetEntry("Projekte/groß.txt")!));
        Assert.Equal("k", Encoding.UTF8.GetString(Read(zip.GetEntry("Projekte/klein.txt")!)));
    }

    [Fact]
    public async Task Several_items_download_as_one_zip_and_a_single_file_directly()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.UploadAsync([WebPage.File("a.txt", "1"), WebPage.File("b.txt", "2")], default);
        browser.Select(browser.Items[0]);
        var single = browser.SelectionDownloadUrl;
        browser.SelectAll();

        using var response = await page.Http.GetAsync(browser.SelectionDownloadUrl);
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());

        Assert.Contains("/download?path=a.txt", single, StringComparison.Ordinal);
        Assert.Equal(["a.txt", "b.txt"], zip.Entries.Select(e => e.FullName));
        Assert.Equal(("SyncDrive.zip", "SyncDrive.zip"), (browser.SelectionDownloadName, response.Content.Headers.ContentDisposition!.FileNameStar));
    }

    [Fact]
    public async Task A_zip_refuses_items_from_different_folders_and_missing_ones()
    {
        var page = await OpenPageAsync();
        var browser = page.Browser;
        await browser.CreateFolderAsync("A", default);
        await browser.UploadAsync([WebPage.File("x.txt", "1")], default);
        var space = browser.Space!.Id;

        using var mixed = await page.Http.GetAsync(page.Api.ZipUrl(space, ["x.txt", "A/y.txt"]));
        using var missing = await page.Http.GetAsync(page.Api.ZipUrl(space, ["fehlt.txt"]));
        using var none = await page.Http.GetAsync($"api/v1/spaces/{space}/zip");

        Assert.Equal(
            (HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.BadRequest),
            (mixed.StatusCode, missing.StatusCode, none.StatusCode));
    }

    [Fact]
    public async Task Another_user_cannot_download_a_zip_of_a_foreign_space()
    {
        var owner = await OpenPageAsync();
        var stranger = await OpenPageAsync();
        await owner.Browser.UploadAsync([WebPage.File("geheim.txt", "x")], default);

        using var response = await stranger.Http.GetAsync(stranger.Api.ZipUrl(owner.Browser.Space!.Id, ["geheim.txt"]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // fremde Spaces gibt es für ihn nicht
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
