using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using SyncDrive.Contracts;
using SyncDrive.Core.Chunking;
using SyncDrive.Server.Host.Tests;
using SyncDrive.Web.App;

namespace SyncDrive.Web.Tests;

/// <summary>
/// Ein Browser mit eigenem Benutzer gegen den echten Host: angemeldet über das Sitzungs-Cookie, ViewModels bereit. <see cref="Requests"/>
/// schreibt „METHODE /pfad“ jeder Anfrage mit. Jeder Test bekommt einen frischen Benutzer, damit „My Drive“ leer beginnt.
/// </summary>
internal sealed record WebPage(string User, HttpClient Http, HttpDriveApi Api, SessionViewModel Session, BrowserViewModel Browser, ConcurrentQueue<string> Requests)
{
    public const string Password = "another-long-password";
    private static readonly ChunkerOptions SmallChunks = new(MinSize: 64, AverageSize: 256, MaxSize: 1024);

    public static async Task<WebPage> OpenAsync(HostFactory factory, string? user = null, string password = Password)
    {
        user ??= await factory.CreateUserAsync("web-" + Guid.NewGuid().ToString("N")[..8]);
        var requests = new ConcurrentQueue<string>();
        var http = factory.CreateDefaultClient(new Uri("http://localhost/"), new CookieContainerHandler(), new Recorder(requests));
        var api = new HttpDriveApi(http, SmallChunks);
        var session = new SessionViewModel(api, WebTexts.English);
        Assert.True(await session.SignInAsync(user, password, default), session.Error);
        var browser = new BrowserViewModel(api, WebTexts.English);
        await browser.InitializeAsync(default);
        return new WebPage(user, http, api, session, browser, requests);
    }

    /// <summary>Öffentlicher Link wie im Freigabedialog: Reiter „Öffentlicher Link“, Häkchen setzen, speichern.</summary>
    public static async Task<LinkInfo> CreateLinkAsync(ShareViewModel share, SpaceSummary space, DriveItem item)
    {
        await share.OpenAsync(space, item, default);
        share.ShareLink.Enabled = true;
        Assert.True(await share.SaveLinkAsync(share.ShareLink, default), share.Message);
        return share.ShareLink.Link!;
    }

    public static UploadFile File(string name, string content) => File(name, Encoding.UTF8.GetBytes(content));

    public static UploadFile File(string name, byte[] content, DateTimeOffset? modified = null) =>
        new(name, content.Length, modified ?? new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero), () => new MemoryStream(content));

    private sealed class Recorder(ConcurrentQueue<string> requests) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
