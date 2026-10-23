using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SyncDrive.Contracts;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Items;
using SyncDrive.Server.App.Sharing;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.Host;

/// <summary>
/// HTTP-Abbild der Use-Cases. Enthält keine Fachlogik. Alles mit <c>{spaceId}</c> läuft über den
/// <see cref="SpaceView"/>, unter dem der Aufrufer den Space kennt (ganz oder geteilter Ordner, ADR-12).
/// </summary>
public static partial class ApiEndpoints
{
    private const string PrincipalKey = "syncdrive.principal";
    private const string ProblemJson = "application/problem+json";
    private const int DefaultPageSize = 500;
    private const int DefaultLongPollSeconds = 30;

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", async (IAccountStore accounts, CancellationToken ct) =>
        {
            await accounts.AnyUserAsync(ct); // wirft, wenn die Datenbank nicht erreichbar ist
            return Results.Text("ok");
        });

        var api = app.MapGroup("/api/v1");
        api.MapPost("/auth/devices", RegisterDevice);
        api.MapGet("/info", () => Results.Ok(new ServerInfo("SyncDrive", ProductVersion.Current)));

        var secured = api.MapGroup(string.Empty).AddEndpointFilter(Authenticate);
        secured.MapDelete("/auth/devices/{deviceId}", RevokeDevice);
        secured.MapGet("/spaces", async (HttpContext http, SharingService sharing, CancellationToken ct) =>
            Results.Ok(await sharing.ListAccessesAsync(Caller(http), ct)));
        secured.MapGet("/spaces/{spaceId}/changes", (string spaceId, long? after, int? limit, HttpContext http, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Previewer, async v => Respond(await v.GetChangesAsync(after ?? 0, limit ?? DefaultPageSize, ct)), ct));
        secured.MapGet("/spaces/{spaceId}/changes/wait", WaitForChanges);
        secured.MapGet("/spaces/{spaceId}/cursor", (string spaceId, HttpContext http, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Previewer, async v => Respond(await v.GetLatestRevisionAsync(ct)), ct));
        secured.MapGet("/spaces/{spaceId}/node", GetNode);
        secured.MapGet("/spaces/{spaceId}/snapshot", GetSnapshot);
        secured.MapGet("/spaces/{spaceId}/usage", (string spaceId, string? path, HttpContext http, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Previewer, async v => Respond(await v.GetUsageAsync(path ?? string.Empty, ct)), ct));
        secured.MapPost("/spaces/{spaceId}/commit", Commit);
        secured.MapGet("/auth/devices", ListDevices);
        secured.MapPost("/spaces/{spaceId}/members", AddMember);
        secured.MapGet("/spaces/{spaceId}/versions", ListVersions);
        secured.MapPost("/spaces/{spaceId}/versions/restore", (string spaceId, RestoreVersionRequest request, HttpContext http, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Editor, async v => Respond(await v.RestoreVersionAsync(request, Caller(http).UserId, ct)), ct));
        secured.MapGet("/spaces/{spaceId}/trash", (string spaceId, HttpContext http, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Viewer, async v => Respond(await v.ListTrashAsync(ct)), ct));
        secured.MapPost("/spaces/{spaceId}/trash/restore", (string spaceId, RestoreTrashRequest request, HttpContext http, CancellationToken ct) =>
            WithView(http, spaceId, SpaceRole.Editor, async v => Respond(await v.RestoreFromTrashAsync(request.NodeId, Caller(http).UserId, ct)), ct));
        secured.MapPut("/spaces/{spaceId}/star", (string spaceId, string? path, HttpContext http, ItemService items, CancellationToken ct) =>
            Star(spaceId, path, starred: true, http, items, ct));
        secured.MapDelete("/spaces/{spaceId}/star", (string spaceId, string? path, HttpContext http, ItemService items, CancellationToken ct) =>
            Star(spaceId, path, starred: false, http, items, ct));
        secured.MapGet("/stars", async (HttpContext http, ItemService items, CancellationToken ct) =>
            Results.Ok(await items.ListStarsAsync(Caller(http), ct)));
        secured.MapGet("/labels", async (HttpContext http, ItemService items, CancellationToken ct) =>
            Results.Ok(await items.ListLabelsAsync(Caller(http), ct)));
        secured.MapPost("/labels", async (CreateLabelRequest request, HttpContext http, ItemService items, CancellationToken ct) =>
            Respond(await items.CreateLabelAsync(Caller(http), request, ct)));
        secured.MapDelete("/labels/{labelId}", async (string labelId, HttpContext http, ItemService items, CancellationToken ct) =>
            await items.DeleteLabelAsync(Caller(http), labelId, ct) ? Results.NoContent() : Problem(new ServiceError(ErrorCode.NotFound, "Label unbekannt.")));
        secured.MapGet("/labels/{labelId}/items", async (string labelId, HttpContext http, ItemService items, CancellationToken ct) =>
            Respond(await items.ListLabeledAsync(Caller(http), labelId, ct)));
        secured.MapPut("/spaces/{spaceId}/labels/{labelId}", (string spaceId, string labelId, string? path, HttpContext http, ItemService items, CancellationToken ct) =>
            Label(spaceId, labelId, path, assigned: true, http, items, ct));
        secured.MapDelete("/spaces/{spaceId}/labels/{labelId}", (string spaceId, string labelId, string? path, HttpContext http, ItemService items, CancellationToken ct) =>
            Label(spaceId, labelId, path, assigned: false, http, items, ct));
        secured.MapGet("/notifications", async (HttpContext http, NotificationService notes, CancellationToken ct) =>
            Results.Ok(await notes.ListAsync(Caller(http), ct)));
        secured.MapPost("/notifications/{id}/read", async (string id, HttpContext http, NotificationService notes, CancellationToken ct) =>
            await notes.MarkReadAsync(Caller(http), id, ct) ? Results.NoContent() : Problem(new ServiceError(ErrorCode.NotFound, "Unbekannt.")));
        secured.MapPost("/spaces/{spaceId}/blocks/missing", MissingBlocks);
        secured.MapPut("/blocks/{hash}", PutBlock);
        secured.MapGet("/spaces/{spaceId}/blocks/{hash}", GetBlock);
        MapWeb(api, secured);
        MapSharing(api, secured);
        MapAdmin(secured);
        MapOAuth(api, secured);
    }

    /// <summary>
    /// Geräte-Token aus <c>Authorization: Bearer</c> (Clients) oder aus dem Sitzungs-Cookie (Browser, ADR-11). Mit Cookie brauchen schreibende
    /// Anfragen die Kopfzeile <see cref="CsrfHeader"/>: Eine fremde Seite kann sie ohne CORS-Freigabe nicht mitschicken.
    /// </summary>
    private static async ValueTask<object?> Authenticate(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (token is null && http.Request.Cookies.TryGetValue(SessionCookie, out var session))
        {
            if (!IsSafeMethod(http.Request.Method) && !http.Request.Headers.ContainsKey(CsrfHeader))
            {
                return Problem(new ServiceError(ErrorCode.Forbidden, $"Kopfzeile {CsrfHeader} fehlt."));
            }

            token = session;
        }

        var principal = await http.RequestServices.GetRequiredService<AccountService>()
            .AuthenticateAsync(token, http.RequestAborted);
        if (principal is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Problem(new ServiceError(ErrorCode.Unauthorized, "Token fehlt oder ist ungültig."));
        }

        http.Items[PrincipalKey] = principal;
        return await next(context);
    }

    /// <summary>Öffnet den Space unter der Id des Aufrufers mit der nötigen Rolle und führt <paramref name="action"/> darauf aus.</summary>
    private static async Task<IResult> WithView(
        HttpContext http, string spaceId, SpaceRole needed, Func<SpaceView, Task<IResult>> action, CancellationToken ct)
    {
        var view = await http.RequestServices.GetRequiredService<SharingService>().OpenAsync(Caller(http), spaceId, needed, ct);
        return view.IsSuccess ? await action(view.Value!) : Problem(view.Error!);
    }

    private static async Task<IResult> RegisterDevice(
        DeviceRegistrationRequest request, HttpContext http, AccountService accounts, CancellationToken ct)
    {
        var (result, retryAfter) = await ThrottledRegisterAsync(http, accounts, request, ct);
        return retryAfter is { } wait ? TooManyAttempts(http, wait) : Respond(result);
    }

    private static async Task<IResult> RevokeDevice(
        string deviceId, HttpContext http, AccountService accounts, CancellationToken ct) =>
        await accounts.RevokeDeviceAsync(Caller(http), deviceId, ct)
            ? Results.NoContent()
            : Problem(new ServiceError(ErrorCode.NotFound, "Gerät unbekannt."));

    private static Task<IResult> WaitForChanges(
        string spaceId, long? after, int? limit, int? timeout, HttpContext http, IOptions<ServerOptions> options, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Previewer, async view =>
        {
            var seconds = Math.Clamp(timeout ?? DefaultLongPollSeconds, 1, options.Value.MaxLongPollSeconds);
            try
            {
                return Respond(await view.WaitForChangesAsync(after ?? 0, limit ?? DefaultPageSize, TimeSpan.FromSeconds(seconds), ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
        }, ct);

    /// <summary>Knoten mit Blockliste; ein Previewer bekommt keine Blöcke (Vorschau ja, Inhalt herunterladen nein).</summary>
    private static Task<IResult> GetNode(string spaceId, string? path, HttpContext http, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Previewer, async view =>
        {
            var result = await view.GetNodeAsync(path ?? string.Empty, ct);
            return result.IsSuccess && view.Role < SpaceRole.Viewer ? Results.Ok(result.Value! with { Blocks = [] }) : Respond(result);
        }, ct);

    /// <summary>NDJSON: ein <see cref="SnapshotEntry"/> je Zeile, damit große Bäume ohne Gesamtpuffer beim Client ankommen.</summary>
    private static Task<IResult> GetSnapshot(string spaceId, string? path, int? depth, HttpContext http, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Previewer, async view =>
        {
            if (depth is < 1)
            {
                return Problem(new ServiceError(ErrorCode.InvalidRequest, "depth muss mindestens 1 sein."));
            }

            var result = await view.GetSnapshotAsync(path ?? string.Empty, ct, depth);
            if (!result.IsSuccess)
            {
                return Problem(result.Error!);
            }

            return Results.Stream(async stream =>
            {
                foreach (var entry in result.Value!)
                {
                    await System.Text.Json.JsonSerializer.SerializeAsync(stream, entry, SnapshotJson, ct);
                    await stream.WriteAsync(NewLine, ct);
                }
            }, "application/x-ndjson");
        }, ct);

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private static readonly System.Text.Json.JsonSerializerOptions SnapshotJson = System.Text.Json.JsonSerializerOptions.Web;

    private static Task<IResult> Commit(string spaceId, CommitRequest request, HttpContext http, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Editor, async view =>
        {
            var key = http.Request.Headers["Idempotency-Key"].ToString();
            return Respond(await view.CommitAsync(request, key.Length == 0 ? null : key, Caller(http).UserId, ct));
        }, ct);

    private static async Task<IResult> ListDevices(HttpContext http, AccountService accounts, CancellationToken ct) =>
        Results.Ok(await accounts.ListDevicesAsync(Caller(http), ct));

    private static async Task<IResult> AddMember(
        string spaceId, AddMemberRequest request, HttpContext http, AccountService accounts, NotificationService notes, CancellationToken ct)
    {
        if (await accounts.AddMemberAsync(Caller(http), spaceId, request, ct) is { } error)
        {
            return Problem(error);
        }

        await notes.SharedAsync(Caller(http), spaceId, request.UserName, ct);
        return Results.NoContent();
    }

    /// <summary>„Gespeichert von“ zeigt den Benutzernamen; intern merkt sich der Server die Benutzer-ID.</summary>
    private static Task<IResult> ListVersions(string spaceId, string? path, HttpContext http, AccountService accounts, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Viewer, async view =>
        {
            var listed = await view.ListVersionsAsync(path ?? string.Empty, ct);
            if (!listed.IsSuccess)
            {
                return Problem(listed.Error!);
            }

            var names = new Dictionary<string, string?>(StringComparer.Ordinal);
            var versions = new List<VersionInfo>();
            foreach (var version in listed.Value!.Versions)
            {
                if (version.CreatedBy is { } userId && !names.ContainsKey(userId))
                {
                    names[userId] = await accounts.UserNameOfAsync(userId, ct);
                }

                versions.Add(version with { CreatedBy = version.CreatedBy is { } id ? names[id] : null });
            }

            return Results.Ok(new VersionsResponse(versions));
        }, ct);

    private static async Task<IResult> Star(
        string spaceId, string? path, bool starred, HttpContext http, ItemService items, CancellationToken ct) =>
        await items.SetStarAsync(Caller(http), spaceId, path ?? string.Empty, starred, ct) is { } error ? Problem(error) : Results.NoContent();

    private static async Task<IResult> Label(
        string spaceId, string labelId, string? path, bool assigned, HttpContext http, ItemService items, CancellationToken ct) =>
        await items.SetLabelAsync(Caller(http), labelId, spaceId, path ?? string.Empty, assigned, ct) is { } error
            ? Problem(error)
            : Results.NoContent();

    /// <summary>Welche Blöcke für einen Commit in diesen Space noch hochzuladen sind (je Space, siehe <see cref="SpaceService"/>).</summary>
    private static Task<IResult> MissingBlocks(string spaceId, MissingBlocksRequest request, HttpContext http, SpaceService spaces, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Editor, async view =>
            Respond(await spaces.MissingBlocksAsync(view.SpaceId, request.Hashes ?? [], Caller(http).UserId, ct)), ct);

    private static async Task<IResult> PutBlock(
        string hash, HttpContext http, SpaceService spaces, IOptions<ServerOptions> options, CancellationToken ct)
    {
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = options.Value.MaxBlockBytes;
        }

        var result = await spaces.PutBlockAsync(hash, http.Request.Body, Caller(http).UserId, ct);
        return result.IsSuccess ? Results.NoContent() : Problem(result.Error!);
    }

    /// <summary>Ein Block, den eine Datei oder Version dieses Space verwendet; Inhalte herunterladen braucht mindestens Viewer.</summary>
    private static Task<IResult> GetBlock(string spaceId, string hash, HttpContext http, SpaceService spaces, CancellationToken ct) =>
        WithView(http, spaceId, SpaceRole.Viewer, async view =>
        {
            var result = await spaces.OpenBlockAsync(view.SpaceId, hash, ct);
            return result.IsSuccess
                ? Results.Stream(result.Value!, "application/octet-stream", enableRangeProcessing: true)
                : Problem(result.Error!);
        }, ct);

    private static Principal Caller(HttpContext http) => (Principal)http.Items[PrincipalKey]!;

    private static IResult Respond<T>(ServiceResult<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    private static IResult Problem(ServiceError error) => Results.Json(
        new ProblemDetail(error.Code, error.Message, error.Current, error.MissingBlocks),
        statusCode: StatusFor(error.Code),
        contentType: ProblemJson);

    private static int StatusFor(string code) => code switch
    {
        ErrorCode.Unauthorized or ErrorCode.PasswordRequired => StatusCodes.Status401Unauthorized,
        ErrorCode.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCode.SpaceNotFound or ErrorCode.NotFound => StatusCodes.Status404NotFound,
        ErrorCode.RevisionConflict or ErrorCode.TypeMismatch or ErrorCode.AlreadyExists
            or ErrorCode.ParentMissing or ErrorCode.BlockHashMismatch => StatusCodes.Status409Conflict,
        ErrorCode.BlocksMissing => StatusCodes.Status422UnprocessableEntity,
        ErrorCode.CursorExpired or ErrorCode.LinkExpired => StatusCodes.Status410Gone,
        ErrorCode.QuotaExceeded => StatusCodes.Status507InsufficientStorage,
        ErrorCode.TooManyAttempts => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status400BadRequest,
    };
}
