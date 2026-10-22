using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Infra.Http;

/// <param name="BaseAddress">Server-Wurzel, z. B. <c>https://nas.example:8443/</c>.</param>
/// <param name="PinnedCertificateSha256">Fingerprint (SHA-256, hex) des beim Einrichten bestätigten Zertifikats; null = normale Prüfung.</param>
public sealed record HttpRemoteOptions(
    Uri BaseAddress, string DeviceToken, string SpaceId, string? PinnedCertificateSha256 = null, ProxyConfig? Proxy = null);

/// <summary>
/// <see cref="IRemoteApi"/> über das HTTPS-Protokoll (/api/v1). Jede Antwort wird zu einem <see cref="Result{T}"/>:
/// Netzfehler und Zeitüberschreitungen sind <c>Transient</c> (<see cref="RemoteErrors.Network"/>), Server-Fehler tragen ihren <c>ErrorCode</c>.
/// </summary>
public sealed class HttpRemoteApi(HttpClient http, string spaceId) : IRemoteApi
{
    /// <summary>Zuschlag auf die Long-Poll-Dauer, bevor der Client selbst aufgibt.</summary>
    private static readonly TimeSpan LongPollGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private string Space => Uri.EscapeDataString(spaceId);

    /// <summary>HttpClient mit Basisadresse <c>…/api/v1/</c>, Geräte-Token und optionalem Zertifikats-Pinning (A4).</summary>
    public static HttpRemoteApi Create(HttpRemoteOptions options)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        Proxies.Apply(handler, options.Proxy);
        if (options.PinnedCertificateSha256 is { } pin)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                CertificatePinning.Accept(certificate as X509Certificate2 ?? (certificate is null ? null : new X509Certificate2(certificate)), errors, pin);
        }

        var client = new HttpClient(handler) { BaseAddress = new Uri(options.BaseAddress, "api/v1/"), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.DeviceToken);
        return new HttpRemoteApi(client, options.SpaceId);
    }

    public async Task<Result<RemoteNode?>> GetNodeAsync(RelativePath path, CancellationToken ct)
    {
        var result = await SendAsync<NodeDetail>(HttpMethod.Get, $"spaces/{Space}/node?path={Uri.EscapeDataString(path.Value)}", null, null, ct);
        if (result.Error is { Code: ErrorCode.NotFound })
        {
            return Result.Ok<RemoteNode?>(null);
        }

        return result.IsSuccess ? Result.Ok<RemoteNode?>(RemoteMapping.ToRemoteNode(result.Value!)) : Result.Fail<RemoteNode?>(result.Error!);
    }

    public async Task<Result<IReadOnlyList<SnapshotEntry>>> GetSnapshotAsync(RelativePath root, CancellationToken ct)
    {
        try
        {
            using var timeout = Timed(RequestTimeout, ct);
            using var response = await http.GetAsync(
                $"spaces/{Space}/snapshot?path={Uri.EscapeDataString(root.Value)}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, timeout.Token);
                return error.Code == ErrorCode.NotFound ? Result.Ok<IReadOnlyList<SnapshotEntry>>([]) : Result.Fail<IReadOnlyList<SnapshotEntry>>(error);
            }

            var entries = new List<SnapshotEntry>();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length > 0)
                {
                    entries.Add(JsonSerializer.Deserialize<SnapshotEntry>(line, Json)!);
                }
            }

            return Result.Ok<IReadOnlyList<SnapshotEntry>>(entries);
        }
        catch (Exception e) when (IsNetworkFailure(e, ct))
        {
            return NetworkFailure<IReadOnlyList<SnapshotEntry>>(e);
        }
    }

    public async Task<Result<long>> GetLatestRevisionAsync(CancellationToken ct)
    {
        var result = await SendAsync<CursorResponse>(HttpMethod.Get, $"spaces/{Space}/cursor", null, null, ct);
        return result.IsSuccess ? Result.Ok(result.Value!.Latest) : Result.Fail<long>(result.Error!);
    }

    public Task<Result<ChangesResponse>> GetChangesAsync(long after, int limit, TimeSpan? wait, CancellationToken ct) =>
        wait is { } w
            ? SendAsync<ChangesResponse>(HttpMethod.Get,
                $"spaces/{Space}/changes/wait?after={after}&limit={limit}&timeout={Math.Max(1, (int)w.TotalSeconds)}", null, null, ct, w + LongPollGrace)
            : SendAsync<ChangesResponse>(HttpMethod.Get, $"spaces/{Space}/changes?after={after}&limit={limit}", null, null, ct);

    public async Task<Result<IReadOnlyList<ContentHash>>> MissingBlocksAsync(IReadOnlyList<ContentHash> hashes, CancellationToken ct)
    {
        var result = await SendAsync<MissingBlocksResponse>(
            HttpMethod.Post, $"spaces/{Space}/blocks/missing", JsonContent.Create(new MissingBlocksRequest([.. hashes.Select(h => h.Value)]), options: Json), null, ct);
        return result.IsSuccess
            ? Result.Ok<IReadOnlyList<ContentHash>>([.. result.Value!.Missing.Select(ContentHash.Parse)])
            : Result.Fail<IReadOnlyList<ContentHash>>(result.Error!);
    }

    public async Task<Result<bool>> UploadBlockAsync(ContentHash hash, Stream content, CancellationToken ct)
    {
        using var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var result = await SendAsync<object>(HttpMethod.Put, $"blocks/{Uri.EscapeDataString(hash.Value)}", body, null, ct);
        return result.IsSuccess ? Result.Ok(true) : Result.Fail<bool>(result.Error!);
    }

    public async Task<Result<Stream>> DownloadBlockAsync(ContentHash hash, CancellationToken ct)
    {
        try
        {
            var response = await http.GetAsync($"spaces/{Space}/blocks/{Uri.EscapeDataString(hash.Value)}", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    return Result.Fail<Stream>(await ReadErrorAsync(response, ct));
                }
            }

            return Result.Ok<Stream>(new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct)));
        }
        catch (Exception e) when (IsNetworkFailure(e, ct))
        {
            return NetworkFailure<Stream>(e);
        }
    }

    public Task<Result<CommitResponse>> CommitAsync(CommitRequest request, string? idempotencyKey, CancellationToken ct) =>
        SendAsync<CommitResponse>(HttpMethod.Post, $"spaces/{Space}/commit", JsonContent.Create(request, options: Json), idempotencyKey, ct);

    private async Task<Result<T>> SendAsync<T>(
        HttpMethod method, string uri, HttpContent? body, string? idempotencyKey, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            using var timed = Timed(timeout ?? RequestTimeout, ct);
            using var request = new HttpRequestMessage(method, uri) { Content = body };
            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            using var response = await http.SendAsync(request, timed.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Fail<T>(await ReadErrorAsync(response, timed.Token));
            }

            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
            {
                return Result.Ok<T>(default!);
            }

            return Result.Ok((await response.Content.ReadFromJsonAsync<T>(Json, timed.Token))!);
        }
        catch (Exception e) when (IsNetworkFailure(e, ct))
        {
            return NetworkFailure<T>(e);
        }
    }

    private static async Task<SyncError> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync<ProblemDetail>(Json, ct) is { Code: { Length: > 0 } code } problem)
            {
                return RemoteErrors.From(code, problem.Title);
            }
        }
        catch (JsonException)
        {
            // kein Problem-JSON (Proxy-Fehlerseite o. Ä.): nach Statuscode einordnen
        }

        var status = (int)response.StatusCode;
        return status >= 500 || status is 408 or 429
            ? new SyncError(SyncErrorKind.Transient, RemoteErrors.Network, $"Server antwortet mit {status}.")
            : new SyncError(SyncErrorKind.Permanent, $"http_{status}", $"Unerwartete Antwort {status}.");
    }

    private static CancellationTokenSource Timed(TimeSpan timeout, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(timeout);
        return source;
    }

    /// <summary>Netz weg oder eigene Zeitüberschreitung; ein Abbruch durch den Aufrufer ist kein Netzfehler und wird durchgereicht.</summary>
    private static bool IsNetworkFailure(Exception e, CancellationToken ct) =>
        e is HttpRequestException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested);

    private static Result<T> NetworkFailure<T>(Exception e) =>
        Result.Fail<T>(SyncErrorKind.Transient, RemoteErrors.Network, e.Message);

    /// <summary>Gibt beim Schließen auch die HTTP-Antwort frei.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Zertifikats-Pinning (A4): Ist ein Fingerprint hinterlegt, gilt genau dieses Zertifikat, auch wenn es selbstsigniert ist. Ohne Pin gilt die
/// normale Kettenprüfung. Einen stillen Schalter „nicht vertrauenswürdig erlauben“ gibt es nicht (01-architektur.md, Sicherheit).
/// </summary>
public static class CertificatePinning
{
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));

    public static bool Accept(X509Certificate2? certificate, System.Net.Security.SslPolicyErrors errors, string pinnedSha256)
    {
        if (certificate is null)
        {
            return false;
        }

        var expected = pinnedSha256.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(Fingerprint(certificate)), System.Text.Encoding.ASCII.GetBytes(expected));
    }
}
