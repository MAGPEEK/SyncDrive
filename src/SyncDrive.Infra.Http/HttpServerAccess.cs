using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Contracts;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Infra.Http;

/// <summary>
/// <see cref="IServerAccess"/> über HTTPS. Die Probe nimmt jedes Zertifikat an, merkt sich aber Fingerabdruck und Prüfergebnis; alle weiteren
/// Anfragen akzeptieren nur ein vom System anerkanntes oder das gepinnte Zertifikat (Vertrauen beim ersten Kontakt nach Rückfrage).
/// </summary>
/// <param name="handlerFactory">Ersetzt in Tests den Netzzugang (In-Process-Server); bekommt den Pin oder null.</param>
/// <param name="proxy">Liefert die aktuelle Proxy-Einstellung (mit Passwort) für jede neue Verbindung.</param>
public sealed class HttpServerAccess(
    Func<string?, HttpMessageHandler>? handlerFactory = null, Func<CancellationToken, Task<ProxyConfig>>? proxy = null) : IServerAccess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<Result<CertificateCheck>> ProbeAsync(Uri server, CancellationToken ct)
    {
        var proxyConfig = proxy is null ? null : await proxy(ct);
        string? fingerprint = null;
        var trusted = true;
        HttpMessageHandler handler;
        if (handlerFactory is not null)
        {
            handler = handlerFactory(null);
        }
        else
        {
            var sockets = new SocketsHttpHandler();
            Proxies.Apply(sockets, proxyConfig);
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is not null)
                {
                    fingerprint = CertificatePinning.Fingerprint(certificate as X509Certificate2 ?? new X509Certificate2(certificate));
                }

                trusted = errors == SslPolicyErrors.None;
                return trusted; // fremdes Zertifikat nur ansehen, nicht annehmen: die Verbindung scheitert, der Fingerabdruck bleibt
            };
            handler = sockets;
        }

        using var http = new HttpClient(handler) { Timeout = Timeout };
        try
        {
            using var response = await http.GetAsync(new Uri(server, "healthz"), ct);
            return Result.Ok(new CertificateCheck(trusted || server.Scheme == Uri.UriSchemeHttp, fingerprint));
        }
        catch (HttpRequestException) when (!trusted && fingerprint is not null)
        {
            return Result.Ok(new CertificateCheck(false, fingerprint)); // erreichbar, aber Zertifikat braucht die Bestätigung des Nutzers
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Result.Fail<CertificateCheck>(SyncErrorKind.Transient, RemoteErrors.Network, e.Message);
        }
    }

    public async Task<Result<DeviceRegistration>> RegisterDeviceAsync(
        Uri server, string userName, string password, string deviceName, string? pinnedCertificateSha256, CancellationToken ct)
    {
        using var http = await ClientAsync(server, null, pinnedCertificateSha256, ct);
        return await SendAsync(async () =>
        {
            using var response = await http.PostAsJsonAsync(
                "auth/devices", new DeviceRegistrationRequest(userName, password, deviceName, "desktop", ProductVersion.Current), Json, ct);
            return await ReadAsync<DeviceRegistrationResponse, DeviceRegistration>(response, r => new DeviceRegistration(r.DeviceId, r.Token), ct);
        }, ct);
    }

    public async Task<Result<IReadOnlyList<SpaceSummary>>> ListSpacesAsync(ServerLogin login, CancellationToken ct)
    {
        using var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        return await SendAsync(async () =>
        {
            using var response = await http.GetAsync("spaces", ct);
            return await ReadAsync<SpacesResponse, IReadOnlyList<SpaceSummary>>(response, r => r.Spaces, ct);
        }, ct);
    }

    public async Task<Result<IReadOnlyList<NotificationInfo>>> ListNotificationsAsync(ServerLogin login, CancellationToken ct)
    {
        using var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        return await SendAsync(async () =>
        {
            using var response = await http.GetAsync("notifications", ct);
            return await ReadAsync<NotificationsResponse, IReadOnlyList<NotificationInfo>>(response, r => r.Notifications, ct);
        }, ct);
    }

    public async Task<Result<bool>> MarkNotificationReadAsync(ServerLogin login, string notificationId, CancellationToken ct)
    {
        using var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        return await SendAsync(async () =>
        {
            using var response = await http.PostAsync($"notifications/{Uri.EscapeDataString(notificationId)}/read", null, ct);
            return response.IsSuccessStatusCode ? Result.Ok(true) : await ReadAsync<object, bool>(response, _ => true, ct);
        }, ct);
    }

    public async Task<Result<UsageResponse>> GetUsageAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct)
    {
        using var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        return await SendAsync(async () =>
        {
            using var response = await http.GetAsync($"spaces/{Uri.EscapeDataString(spaceId)}/usage?path={Uri.EscapeDataString(path.Value)}", ct);
            return await ReadAsync<UsageResponse, UsageResponse>(response, r => r, ct);
        }, ct);
    }

    public async Task<Result<IReadOnlyList<VersionInfo>>> ListVersionsAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct)
    {
        using var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        return await SendAsync(async () =>
        {
            using var response = await http.GetAsync($"spaces/{Uri.EscapeDataString(spaceId)}/versions?path={Uri.EscapeDataString(path.Value)}", ct);
            return await ReadAsync<VersionsResponse, IReadOnlyList<VersionInfo>>(response, r => r.Versions, ct);
        }, ct);
    }

    public async Task<Result<CommitResponse>> RestoreVersionAsync(
        ServerLogin login, string spaceId, RelativePath path, long revision, long baseRevision, CancellationToken ct)
    {
        using var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        return await SendAsync(async () =>
        {
            using var response = await http.PostAsJsonAsync(
                $"spaces/{Uri.EscapeDataString(spaceId)}/versions/restore", new RestoreVersionRequest(path.Value, revision, baseRevision), Json, ct);
            return await ReadAsync<CommitResponse, CommitResponse>(response, r => r, ct);
        }, ct);
    }

    public async Task<Result<NodeInfo?>> GetNodeInfoAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct)
    {
        using var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        return await SendAsync(async () =>
        {
            using var response = await http.GetAsync($"spaces/{Uri.EscapeDataString(spaceId)}/node?path={Uri.EscapeDataString(path.Value)}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Result.Ok<NodeInfo?>(null);
            }

            return await ReadAsync<NodeDetail, NodeInfo?>(response, d => d.Node, ct);
        }, ct);
    }

    public async Task<Result<Stream>> DownloadBlockAsync(ServerLogin login, string spaceId, string hash, CancellationToken ct)
    {
        var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        try
        {
            var response = await http.GetAsync($"spaces/{Uri.EscapeDataString(spaceId)}/blocks/{Uri.EscapeDataString(hash)}", HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    return await ReadAsync<object, Stream>(response, _ => Stream.Null, ct);
                }
            }

            var content = new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)); // Blöcke sind höchstens 4 MiB
            response.Dispose();
            return Result.Ok<Stream>(content);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Result.Fail<Stream>(SyncErrorKind.Transient, RemoteErrors.Network, e.Message);
        }
        finally
        {
            http.Dispose();
        }
    }

    public async Task<Result<IReadOnlyList<SnapshotEntry>>> ListChildrenAsync(ServerLogin login, string spaceId, RelativePath path, CancellationToken ct)
    {
        using var http = await ClientAsync(login.Server, login.DeviceToken, login.PinnedCertificateSha256, ct);
        return await SendAsync<IReadOnlyList<SnapshotEntry>>(async () =>
        {
            using var response = await http.GetAsync(
                $"spaces/{Uri.EscapeDataString(spaceId)}/snapshot?path={Uri.EscapeDataString(path.Value)}&depth=1", ct);
            if (!response.IsSuccessStatusCode)
            {
                return await ReadAsync<object, IReadOnlyList<SnapshotEntry>>(response, _ => [], ct);
            }

            var entries = new List<SnapshotEntry>();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 0 && JsonSerializer.Deserialize<SnapshotEntry>(line, Json) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            return Result.Ok<IReadOnlyList<SnapshotEntry>>(entries);
        }, ct);
    }

    private async Task<HttpClient> ClientAsync(Uri server, string? token, string? pin, CancellationToken ct)
    {
        var proxyConfig = proxy is null ? null : await proxy(ct);
        HttpMessageHandler handler;
        if (handlerFactory is not null)
        {
            handler = handlerFactory(pin);
        }
        else
        {
            var sockets = new SocketsHttpHandler();
            Proxies.Apply(sockets, proxyConfig);
            if (pin is not null)
            {
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    CertificatePinning.Accept(certificate as X509Certificate2 ?? (certificate is null ? null : new X509Certificate2(certificate)), errors, pin);
            }

            handler = sockets;
        }

        var http = new HttpClient(handler) { BaseAddress = new Uri(server, "api/v1/"), Timeout = Timeout };
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }

    private static async Task<Result<T>> SendAsync<T>(Func<Task<Result<T>>> send, CancellationToken ct)
    {
        try
        {
            return await send();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Result.Fail<T>(SyncErrorKind.Transient, RemoteErrors.Network, e.Message);
        }
    }

    private static async Task<Result<TOut>> ReadAsync<TIn, TOut>(HttpResponseMessage response, Func<TIn, TOut> map, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<TIn>(Json, ct);
            return body is null ? Result.Fail<TOut>(SyncErrorKind.Transient, "empty_response", "Leere Antwort.") : Result.Ok(map(body));
        }

        var problem = await TryReadProblemAsync(response, ct);
        var kind = (int)response.StatusCode >= 500 ? SyncErrorKind.Transient : SyncErrorKind.Permanent;
        return Result.Fail<TOut>(kind, problem?.Code ?? $"http_{(int)response.StatusCode}", problem?.Title ?? response.ReasonPhrase ?? string.Empty);
    }

    private static async Task<ProblemDetail?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDetail>(Json, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
