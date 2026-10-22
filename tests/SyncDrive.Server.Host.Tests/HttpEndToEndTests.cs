using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Mvc.Testing;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Chunking;
using SyncDrive.Core.Filtering;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;
using SyncDrive.Infra.Fs;
using SyncDrive.Infra.Http;
using SyncDrive.Infra.Sqlite.Client;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Echter Client-Stack (Ordner, SQLite-Basis, HTTP) gegen den echten Server-Host (SQLite, Blockspeicher, Argon2).</summary>
public sealed class HttpEndToEndTests(HostFactory factory) : IClassFixture<HostFactory>, IDisposable
{
    private static readonly ChunkerOptions SmallChunks = new(MinSize: 256, AverageSize: 1024, MaxSize: 4096);
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var dir in _directories)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private async Task<HttpRemoteApi> RemoteAsync(ApiClient? login = null, string? spaceId = null)
    {
        login ??= await factory.LoginAsync();
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost/api/v1/") });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Registration.Token);
        return new HttpRemoteApi(http, spaceId ?? await login.FirstSpaceIdAsync());
    }

    private async Task<Device> DeviceAsync(string name, HttpRemoteApi? remote = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"syncdrive-e2e-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _directories.Add(root);
        var db = Path.Combine(root, ".syncdrive-session.db");
        var local = new LocalFileStore(Path.Combine(root, "Drive"));
        var state = new SqliteSessionState(db);
        var log = new SqliteActivityLog(db);
        remote ??= await RemoteAsync();
        var config = new SyncTaskConfig(name, SyncDirection.TwoWay, new ClientSettings(), new FilterEngine([]), SmallChunks);
        var engine = new SyncEngine(local, remote, state, config, log, TimeProvider.System);
        return new Device(Path.Combine(root, "Drive"), new SyncSession(engine, local, remote, state), log);
    }

    private sealed record Device(string Root, SyncSession Session, SqliteActivityLog Log)
    {
        public string Full(string p) => Path.Combine(Root, p.Replace('/', Path.DirectorySeparatorChar));

        public async Task StartAsync()
        {
            var report = await Session.StartAsync(startup: true, CancellationToken.None);
            Assert.True(report.Failures.Count == 0, string.Join("; ", report.Failures.Select(f => $"{f.Path}: {f.Error.Code} {f.Error.Message}")));
        }
    }

    [Fact]
    public async Task Two_devices_sync_files_over_http_including_feed_long_poll_and_delete()
    {
        // Ein Space für diesen Test, damit andere Tests der Klasse nicht stören.
        var a = await DeviceAsync("A");
        var b = await DeviceAsync("B");
        var folder = "e2e-" + Guid.NewGuid().ToString("N")[..8];
        var big = new byte[300_000];
        new Random(5).NextBytes(big);
        Directory.CreateDirectory(a.Full(folder));
        await File.WriteAllTextAsync(a.Full($"{folder}/hallo.txt"), "Hallo über HTTP");
        await File.WriteAllBytesAsync(a.Full($"{folder}/gross.bin"), big);

        await a.StartAsync();
        await b.StartAsync();

        Assert.Equal("Hallo über HTTP", await File.ReadAllTextAsync(b.Full($"{folder}/hallo.txt")));
        Assert.Equal(big, await File.ReadAllBytesAsync(b.Full($"{folder}/gross.bin")));

        var waiting = b.Session.PullChangesAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        await File.WriteAllTextAsync(a.Full($"{folder}/hallo.txt"), "geändert");
        var upload = await a.Session.EvaluateWithRetryAsync(RelativePath.Parse($"{folder}/hallo.txt"), startup: false, CancellationToken.None);
        Assert.Equal(OutcomeKind.Uploaded, upload.Value!.Kind);
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.Equal("geändert", await File.ReadAllTextAsync(b.Full($"{folder}/hallo.txt")));

        File.Delete(a.Full($"{folder}/gross.bin"));
        await a.Session.EvaluateWithRetryAsync(RelativePath.Parse($"{folder}/gross.bin"), startup: false, CancellationToken.None);
        Assert.True((await b.Session.PullChangesAsync(null, CancellationToken.None)).IsSuccess);
        Assert.False(File.Exists(b.Full($"{folder}/gross.bin")));

        var recent = await b.Log.RecentAsync(10, CancellationToken.None);
        Assert.Contains(recent, e => e is { Action: ActivityAction.Delete, Result: ActivityResult.Done });
        Assert.Contains(recent, e => e is { Action: ActivityAction.Download, Result: ActivityResult.Done });
    }

    [Fact]
    public async Task Unknown_paths_are_not_errors_and_missing_blocks_come_back()
    {
        var remote = await RemoteAsync();

        var node = await remote.GetNodeAsync(RelativePath.Parse("gibt-es-nicht-" + Guid.NewGuid().ToString("N")), CancellationToken.None);
        var hash = Core.Model.ContentHash.FromBytes(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var missing = await remote.MissingBlocksAsync([hash], CancellationToken.None);

        Assert.True(node.IsSuccess);
        Assert.Null(node.Value);
        Assert.Equal([hash], missing.Value);
    }

    [Fact]
    public async Task A_revoked_device_gets_a_fatal_unauthorized_error()
    {
        var login = await factory.LoginAsync();
        var remote = await RemoteAsync(login);
        await login.Http.DeleteAsync($"/api/v1/auth/devices/{login.Registration.DeviceId}");

        var result = await remote.GetLatestRevisionAsync(CancellationToken.None);

        Assert.Equal((SyncErrorKind.Fatal, "unauthorized"), (result.Error!.Kind, result.Error.Code));
    }

    [Fact]
    public async Task An_unreachable_server_is_a_transient_network_error()
    {
        var remote = HttpRemoteApi.Create(new HttpRemoteOptions(new Uri("http://127.0.0.1:9/"), "sd_x", "space"));

        var result = await remote.GetLatestRevisionAsync(CancellationToken.None);

        Assert.Equal((SyncErrorKind.Transient, RemoteErrors.Network), (result.Error!.Kind, result.Error.Code));
    }

    [Fact]
    public void Certificate_pinning_accepts_exactly_the_pinned_certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=syncdrive-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var other = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        var pin = CertificatePinning.Fingerprint(cert);
        var colonPin = string.Join(':', Enumerable.Range(0, pin.Length / 2).Select(i => pin.Substring(i * 2, 2))).ToUpperInvariant();
        const System.Net.Security.SslPolicyErrors untrusted = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;

        Assert.True(CertificatePinning.Accept(cert, untrusted, pin));
        Assert.True(CertificatePinning.Accept(cert, untrusted, colonPin));
        Assert.False(CertificatePinning.Accept(other, System.Net.Security.SslPolicyErrors.None, pin));
        Assert.False(CertificatePinning.Accept(null, System.Net.Security.SslPolicyErrors.None, pin));
    }

    /// <summary>M6b (ADR-12): Ein geteilter Ordner ist für den Client ein Space wie jeder andere; der Server rechnet Pfade und Feed um.</summary>
    [Fact]
    public async Task A_desktop_client_syncs_a_folder_shared_with_its_user_like_a_space_of_its_own()
    {
        var annaName = await factory.CreateUserAsync("anna-" + Guid.NewGuid().ToString("N")[..8]);
        var benName = await factory.CreateUserAsync("ben-" + Guid.NewGuid().ToString("N")[..8]);
        var annaLogin = await factory.LoginAsync(annaName, "another-long-password");
        var anna = await DeviceAsync("Anna", await RemoteAsync(annaLogin));
        Directory.CreateDirectory(anna.Full("Projekte"));
        await File.WriteAllTextAsync(anna.Full("Projekte/plan.txt"), "Plan");
        await File.WriteAllTextAsync(anna.Full("geheim.txt"), "nur Anna");
        await anna.StartAsync();
        var space = await annaLogin.FirstSpaceIdAsync();
        using var invite = await annaLogin.Http.PostAsJsonAsync(
            $"/api/v1/spaces/{space}/sharing/invitees?path=Projekte", new Contracts.InviteRequest(benName, "editor"));
        var shareId = (await invite.Content.ReadFromJsonAsync<Contracts.InviteeInfo>())!.ShareId;
        var ben = await DeviceAsync("Ben", await RemoteAsync(await factory.LoginAsync(benName, "another-long-password"), shareId));

        await ben.StartAsync();
        var benFiles = Directory.GetFiles(ben.Root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(ben.Root, p));
        await File.WriteAllTextAsync(ben.Full("von-ben.txt"), "Hallo Anna");
        await ben.Session.EvaluateWithRetryAsync(RelativePath.Parse("von-ben.txt"), startup: false, CancellationToken.None);
        Assert.True((await anna.Session.PullChangesAsync(null, CancellationToken.None)).IsSuccess);
        await File.WriteAllTextAsync(anna.Full("Projekte/plan.txt"), "Plan v2");
        await File.WriteAllTextAsync(anna.Full("noch-geheimer.txt"), "x");
        await anna.Session.EvaluateWithRetryAsync(RelativePath.Parse("Projekte/plan.txt"), startup: false, CancellationToken.None);
        await anna.Session.EvaluateWithRetryAsync(RelativePath.Parse("noch-geheimer.txt"), startup: false, CancellationToken.None);
        Assert.True((await ben.Session.PullChangesAsync(null, CancellationToken.None)).IsSuccess);

        Assert.Equal(["plan.txt"], benFiles);
        Assert.Equal("Hallo Anna", await File.ReadAllTextAsync(anna.Full("Projekte/von-ben.txt")));
        Assert.Equal("Plan v2", await File.ReadAllTextAsync(ben.Full("plan.txt")));
        Assert.False(File.Exists(ben.Full("noch-geheimer.txt")));
        Assert.False(File.Exists(ben.Full("geheim.txt")));
    }
}
