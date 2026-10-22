using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>M7 Härtung über HTTP: zu viele falsche Passwörter werden mit 429 gebremst; andere Benutzer bleiben unberührt.</summary>
public class ThrottleApiTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";

    private async Task<HttpResponseMessage> SignInAsync(string user, string password) =>
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/devices", new DeviceRegistrationRequest(user, password, "Test"));

    [Fact]
    public async Task Repeated_wrong_passwords_lock_this_user_for_a_while_but_not_others()
    {
        var victim = await factory.CreateUserAsync("opfer-" + Guid.NewGuid().ToString("N")[..8]);
        var bystander = await factory.CreateUserAsync("andere-" + Guid.NewGuid().ToString("N")[..8]);

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await SignInAsync(victim, "falsch-" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await SignInAsync(victim, Password); // auch das richtige Passwort wird jetzt nicht geprüft
        var problem = await locked.Content.ReadFromJsonAsync<ProblemDetail>();
        using var other = await SignInAsync(bystander, Password);

        Assert.Equal((HttpStatusCode.TooManyRequests, ErrorCode.TooManyAttempts), (locked.StatusCode, problem!.Code));
        Assert.InRange(int.Parse(locked.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture), 1, 900);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task The_browser_sign_in_is_braked_too()
    {
        var user = await factory.CreateUserAsync("web-" + Guid.NewGuid().ToString("N")[..8]);
        var web = factory.CreateClient();
        web.DefaultRequestHeaders.Add("X-SyncDrive-Web", "1");

        for (var i = 0; i < 5; i++)
        {
            (await web.PostAsJsonAsync("/api/v1/web/session", new WebSignInRequest(user, "falsch"))).Dispose();
        }

        using var locked = await web.PostAsJsonAsync("/api/v1/web/session", new WebSignInRequest(user, Password));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Guessing_a_link_password_is_braked()
    {
        var owner = await factory.LoginAsync(await factory.CreateUserAsync("link-" + Guid.NewGuid().ToString("N")[..8]), Password);
        var space = await owner.FirstSpaceIdAsync();
        (await owner.Http.PostAsJsonAsync($"/api/v1/spaces/{space}/commit", new CommitRequest(CommitOps.Mkdir, "Geteilt", 0, MTime: DateTimeOffset.UtcNow)))
            .EnsureSuccessStatusCode();
        using var created = await owner.Http.PutAsJsonAsync($"/api/v1/spaces/{space}/sharing/link?path=Geteilt&kind=share", new LinkRequest(null, "richtig-1"));
        var link = (await created.Content.ReadFromJsonAsync<LinkInfo>())!;
        var visitor = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        for (var i = 0; i < 10; i++)
        {
            (await visitor.PostAsJsonAsync($"/api/v1/public/{link.Token}/password", new LinkPasswordRequest("falsch-" + i))).Dispose();
        }

        using var locked = await visitor.PostAsJsonAsync($"/api/v1/public/{link.Token}/password", new LinkPasswordRequest("richtig-1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }
}
