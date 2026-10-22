using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Anmeldung im Browser (Sitzungs-Cookie, Schutz vor fremden Seiten) und Herunterladen als Datenstrom (ADR-11).</summary>
public class WebApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Csrf = ApiEndpoints.CsrfHeader;
    private static readonly string[] Parts = ["erster Block, ", "zweiter Block"];

    /// <summary>Ein Browser: hält Cookies, schickt die Kopfzeile der eigenen Seite mit, wenn <paramref name="ownPage"/>.</summary>
    private HttpClient Browser(bool ownPage = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0) Gecko/20100101 Firefox/130.0");
        if (ownPage)
        {
            client.DefaultRequestHeaders.Add(Csrf, "1");
        }

        return client;
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient browser, string password = HostFactory.AdminPassword) =>
        browser.PostAsJsonAsync("/api/v1/web/session", new WebSignInRequest(HostFactory.AdminName, password));

    [Fact]
    public async Task Signing_in_sets_an_http_only_strict_cookie_that_authenticates_the_api()
    {
        var browser = Browser();

        var response = await SignInAsync(browser);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(ApiEndpoints.SessionCookie + "=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new WebSessionInfo(HostFactory.AdminName, true), await browser.GetFromJsonAsync<WebSessionInfo>("/api/v1/web/session"));
        Assert.NotEmpty((await browser.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!.Spaces);
    }

    [Fact]
    public async Task A_wrong_password_sets_no_cookie()
    {
        var browser = Browser();

        var response = await SignInAsync(browser, "falsch");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Without_the_page_header_a_foreign_site_can_neither_sign_in_nor_write_with_the_cookie()
    {
        var browser = Browser();
        await SignInAsync(browser);
        var spaceId = (await browser.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!.Spaces[0].Id;
        browser.DefaultRequestHeaders.Remove(Csrf); // wie ein Formular einer fremden Seite: Cookie ja, Kopfzeile nein

        var write = await browser.PostAsJsonAsync($"/api/v1/spaces/{spaceId}/commit", new CommitRequest(CommitOps.Mkdir, "fremd", 0));
        var read = await browser.GetAsync("/api/v1/spaces");
        var login = await SignInAsync(Browser(ownPage: false));

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.Forbidden), (write.StatusCode, read.StatusCode, login.StatusCode));
    }

    [Fact]
    public async Task Each_browser_sign_in_is_a_web_device_and_signing_out_revokes_it()
    {
        var browser = Browser();
        await SignInAsync(browser);
        var device = Assert.Single((await browser.GetFromJsonAsync<DevicesResponse>("/api/v1/auth/devices"))!.Devices, d => d.IsCurrent);

        var signOut = await browser.DeleteAsync("/api/v1/web/session");
        var afterwards = await browser.GetAsync("/api/v1/web/session");

        Assert.Equal(("Browser (Firefox)", "web"), (device.Name, device.ClientType));
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.Unauthorized), (signOut.StatusCode, afterwards.StatusCode));
    }

    [Fact]
    public async Task A_file_downloads_as_one_stream_with_its_name_and_folders_do_not()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var folder = "web-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True((await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, folder, 0))).IsSuccessStatusCode);
        var parts = Parts.Select(System.Text.Encoding.UTF8.GetBytes).ToArray();
        foreach (var part in parts)
        {
            using var body = new ByteArrayContent(part);
            (await api.Http.PutAsync($"/api/v1/blocks/{HashOf(part)}", body)).EnsureSuccessStatusCode();
        }

        var all = parts.SelectMany(p => p).ToArray();
        var put = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, folder + "/Bericht 1.txt", 0, Blocks: [.. parts.Select(HashOf)], Size: all.Length, MTime: DateTimeOffset.UtcNow,
            FileHash: HashOf(all)));
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        var browser = Browser();
        await SignInAsync(browser);

        var file = await browser.GetAsync($"/api/v1/spaces/{space}/download?path={Uri.EscapeDataString(folder + "/Bericht 1.txt")}");
        var directory = await browser.GetAsync($"/api/v1/spaces/{space}/download?path={Uri.EscapeDataString(folder)}");

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("erster Block, zweiter Block", await file.Content.ReadAsStringAsync());
        Assert.Equal("Bericht 1.txt", file.Content.Headers.ContentDisposition?.FileNameStar ?? file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(HttpStatusCode.NotFound, directory.StatusCode);
    }

    [Fact]
    public async Task The_web_app_is_served_at_the_root_with_strict_security_headers()
    {
        var browser = Browser();

        var page = await browser.GetAsync("/");
        var runtime = await browser.GetAsync("/_framework/blazor.webassembly.js");
        var api = await browser.GetAsync("/api/v1/web/session");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<div id=\"app\">", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var csp = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", csp, StringComparison.Ordinal); // nur 'wasm-unsafe-eval'
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode); // die API bleibt JSON, keine Seite statt 404/401
    }

    [Fact]
    public async Task Search_finds_names_below_a_folder_and_recent_lists_files_newest_first()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var root = "find-" + Guid.NewGuid().ToString("N")[..8];
        await MkdirAsync(api, space, root);
        await MkdirAsync(api, space, root + "/Berichte");
        await PutTextAsync(api, space, root + "/Berichte/Jahresbericht.txt", "1", DateTimeOffset.UtcNow.AddDays(-2));
        await PutTextAsync(api, space, root + "/notiz.txt", "2", DateTimeOffset.UtcNow.AddDays(1));
        await PutTextAsync(api, space, root + "/BERICHT-alt.txt", "3", DateTimeOffset.UtcNow.AddDays(-9));

        var found = (await api.Http.GetFromJsonAsync<FindResponse>(
            $"/api/v1/spaces/{space}/search?q=bericht&path={Uri.EscapeDataString(root)}"))!.Entries;
        var outside = (await api.Http.GetFromJsonAsync<FindResponse>(
            $"/api/v1/spaces/{space}/search?q=bericht&path={Uri.EscapeDataString(root + "/Berichte")}"))!.Entries;
        var empty = await api.Http.GetAsync($"/api/v1/spaces/{space}/search?q=%20");
        var recent = (await api.Http.GetFromJsonAsync<FindResponse>($"/api/v1/spaces/{space}/recent?limit=200"))!.Entries
            .Where(e => e.Path.StartsWith(root + "/", StringComparison.Ordinal)).Select(e => e.Path).ToList();

        Assert.Equal([root + "/Berichte", root + "/BERICHT-alt.txt", root + "/Berichte/Jahresbericht.txt"], found.Select(e => e.Path));
        Assert.Equal([root + "/Berichte/Jahresbericht.txt"], outside.Select(e => e.Path));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal([root + "/notiz.txt", root + "/Berichte/Jahresbericht.txt", root + "/BERICHT-alt.txt"], recent);
    }

    [Fact]
    public async Task An_older_version_downloads_by_revision()
    {
        var api = await factory.LoginAsync();
        var space = await api.FirstSpaceIdAsync();
        var file = "version-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var first = await PutTextAsync(api, space, file, "erste Fassung", DateTimeOffset.UtcNow);
        await PutTextAsync(api, space, file, "zweite Fassung", DateTimeOffset.UtcNow, first);
        var browser = Browser();
        await SignInAsync(browser);
        var url = $"/api/v1/spaces/{space}/download?path={Uri.EscapeDataString(file)}";

        Assert.Equal("zweite Fassung", await browser.GetStringAsync(url));
        Assert.Equal("erste Fassung", await browser.GetStringAsync(url + "&revision=" + first));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(url + "&revision=999999")).StatusCode);
    }

    private static async Task MkdirAsync(ApiClient api, string space, string path) =>
        Assert.True((await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, path, 0))).IsSuccessStatusCode);

    /// <summary>Datei aus einem Block; Ergebnis ist die neue Revision.</summary>
    private static async Task<long> PutTextAsync(ApiClient api, string space, string path, string text, DateTimeOffset mtime, long baseRevision = 0)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using (var body = new ByteArrayContent(bytes))
        {
            (await api.Http.PutAsync($"/api/v1/blocks/{HashOf(bytes)}", body)).EnsureSuccessStatusCode();
        }

        var put = await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, path, baseRevision, Blocks: [HashOf(bytes)], Size: bytes.Length, MTime: mtime, FileHash: HashOf(bytes)));
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        return (await put.Content.ReadFromJsonAsync<CommitResponse>())!.Revision;
    }

    private static string HashOf(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
}
