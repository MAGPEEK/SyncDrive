using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Ansehen im Browser (0.34.0): nur PDF inline, nur in der eigenen Oberfläche einbettbar, nur mit Leserecht.</summary>
public class ViewEndpointTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";

    private static async Task PutAsync(ApiClient api, string space, string path, byte[] bytes)
    {
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        using (var body = new ByteArrayContent(bytes))
        {
            (await api.Http.PutAsync($"/api/v1/blocks/{hash}", body)).EnsureSuccessStatusCode();
        }

        (await api.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(
            CommitOps.Put, path, 0, Blocks: [hash], Size: bytes.Length, MTime: DateTimeOffset.UtcNow, FileHash: hash))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_pdf_is_served_inline_for_the_own_page_and_other_types_are_refused()
    {
        var anna = await factory.LoginAsync(await factory.CreateUserAsync("anna-" + Guid.NewGuid().ToString("N")[..8]), Password);
        var ben = await factory.LoginAsync(await factory.CreateUserAsync("ben-" + Guid.NewGuid().ToString("N")[..8]), Password);
        var space = await anna.FirstSpaceIdAsync();
        var pdf = "%PDF-1.4\n%Test\n"u8.ToArray();
        await PutAsync(anna, space, "Bericht.pdf", pdf);
        await PutAsync(anna, space, "seite.html", "<script>alert(1)</script>"u8.ToArray());

        using var view = await anna.Http.GetAsync($"/api/v1/spaces/{space}/view?path=Bericht.pdf");
        using var html = await anna.Http.GetAsync($"/api/v1/spaces/{space}/view?path=seite.html");
        using var foreign = await ben.Http.GetAsync($"/api/v1/spaces/{space}/view?path=Bericht.pdf");
        using var missing = await anna.Http.GetAsync($"/api/v1/spaces/{space}/view?path=fehlt.pdf");

        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal("application/pdf", view.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", view.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(pdf, await view.Content.ReadAsByteArrayAsync());
        Assert.Equal("frame-ancestors 'self'", view.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("SAMEORIGIN", view.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, html.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
