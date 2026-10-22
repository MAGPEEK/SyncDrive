using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Startet den echten Host (SQLite, Dateispeicher, Argon2) mit einem Temp-Datenordner.</summary>
public class HostFactory : WebApplicationFactory<Program>
{
    public const string AdminName = "admin";
    public const string AdminPassword = "correct-horse-battery";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "syncdrive-host-" + Guid.NewGuid().ToString("N"));

    protected virtual bool RequireTls => false;

    protected virtual bool TrustForwardedHeaders => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Server:DataDirectory"] = _dataDirectory,
                ["Server:RequireTls"] = RequireTls.ToString(),
                ["Server:TrustForwardedHeaders"] = TrustForwardedHeaders.ToString(),
                ["Server:AdminUserName"] = AdminName,
                ["Server:AdminPassword"] = AdminPassword,
                ["Server:Argon2MemoryKiB"] = "64", // schnell genug für Tests, gleicher Code-Pfad
                ["Server:Argon2Iterations"] = "1",
                ["Server:Argon2Parallelism"] = "1",
            }));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataDirectory))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // gepoolte Verbindungen halten die Datei offen
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    public async Task<ApiClient> LoginAsync(string userName = AdminName, string password = AdminPassword)
    {
        var anonymous = CreateClient();
        var response = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/devices", new DeviceRegistrationRequest(userName, password, "Testgerät"));
        response.EnsureSuccessStatusCode();
        var registration = (await response.Content.ReadFromJsonAsync<DeviceRegistrationResponse>())!;
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
        return new ApiClient(client, registration);
    }

    public async Task<string> CreateUserAsync(string name)
    {
        var accounts = Services.GetRequiredService<AccountService>();
        var result = await accounts.CreateUserAsync(name, "another-long-password", false, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return name;
    }
}

public sealed class TlsRequiredFactory : HostFactory
{
    protected override bool RequireTls => true;
}

public sealed class ProxiedTlsFactory : HostFactory
{
    protected override bool RequireTls => true;

    protected override bool TrustForwardedHeaders => true;
}

public sealed record ApiClient(HttpClient Http, DeviceRegistrationResponse Registration)
{
    public async Task<string> FirstSpaceIdAsync()
    {
        var spaces = (await Http.GetFromJsonAsync<SpacesResponse>("/api/v1/spaces"))!;
        return spaces.Spaces[0].Id;
    }
}
