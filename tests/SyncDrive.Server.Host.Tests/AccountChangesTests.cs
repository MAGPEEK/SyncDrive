using System.Net.Http.Json;
using SyncDrive.Contracts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Echtzeit der Weboberfläche (0.32.0): ein Stand über alles, was der Benutzer sieht, und nur das.</summary>
public class AccountChangesTests(HostFactory factory) : IClassFixture<HostFactory>
{
    private const string Password = "another-long-password";

    private async Task<(string User, ApiClient Client)> UserAsync(string prefix)
    {
        var user = await factory.CreateUserAsync(prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        return (user, await factory.LoginAsync(user, Password));
    }

    private static async Task<string> StampAsync(ApiClient client, string? after = null, int timeout = 1)
    {
        var url = after is null ? "/api/v1/account/changes/wait" : $"/api/v1/account/changes/wait?after={after}&timeout={timeout}";
        return (await client.Http.GetFromJsonAsync<AccountChangesResponse>(url))!.Stamp;
    }

    private static async Task<string> OwnSpaceAsync(ApiClient client) =>
        (await client.Http.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!.Spaces[0].Id;

    [Fact]
    public async Task The_wait_ends_with_a_change_in_an_own_space_but_not_with_one_of_another_user()
    {
        var (_, anna) = await UserAsync("anna");
        var (_, ben) = await UserAsync("ben");
        var start = await StampAsync(anna);

        var unchanged = await StampAsync(anna, start); // Zeit abgelaufen
        await ben.Http.PostAsJsonAsync($"/api/v1/spaces/{await OwnSpaceAsync(ben)}/commit", new CommitRequest(CommitOps.Mkdir, "fremd", 0));
        var afterForeign = await StampAsync(anna, start);
        var waiting = StampAsync(anna, start, timeout: 20);
        await Task.Delay(300);
        await anna.Http.PostAsJsonAsync($"/api/v1/spaces/{await OwnSpaceAsync(anna)}/commit", new CommitRequest(CommitOps.Mkdir, "eigen", 0));
        var afterOwn = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal((start, start), (unchanged, afterForeign));
        Assert.NotEqual(start, afterOwn);
    }

    [Fact]
    public async Task Being_added_to_a_space_wakes_the_new_member()
    {
        var (_, owner) = await UserAsync("owner");
        var (member, memberClient) = await UserAsync("member");
        var start = await StampAsync(memberClient);
        var waiting = StampAsync(memberClient, start, timeout: 20);
        await Task.Delay(300);

        using var add = await owner.Http.PostAsJsonAsync(
            $"/api/v1/spaces/{await OwnSpaceAsync(owner)}/members", new AddMemberRequest(member, "viewer"));
        var after = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(add.IsSuccessStatusCode);
        Assert.NotEqual(start, after);
    }
}
