using SyncDrive.Contracts;
using SyncDrive.Core.Chunking;
using SyncDrive.Core.Paths;
using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Policies;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Sharing;

/// <summary>Ein gültiger öffentlicher Link: das Ziel (Datei oder Ordner) und, bei Ordnern, der View darauf (nur lesen).</summary>
public sealed record PublicAccess(LinkRecord Link, NodeInfo Target, SpaceView View, string? Owner)
{
    public bool IsFolder => Target.Kind == NodeKinds.Directory;
}

/// <summary>
/// Öffentliche Links ohne Anmeldung (M6b): ansehen und herunterladen (<see cref="LinkKinds.Share"/>) oder als Dateianfrage hochladen
/// (<see cref="LinkKinds.Request"/>). Der Link folgt seinem Ziel über die Knoten-Id, auch nach Umbenennen oder Verschieben. Ob ein
/// Passwort schon eingegeben wurde, merkt sich die Host-Schicht (Cookie); hier wird es nur geprüft.
/// </summary>
public sealed class PublicLinkService(
    IShareStore shares, SharingService sharing, SpaceService spaces, AccountService accounts, IPasswordHasher hasher, TimeProvider clock, PolicyService policies)
{
    private const int MaxUniqueNameAttempts = 1000;
    private const string NoUploads = "Dieser Link nimmt keine Dateien an.";

    public async Task<ServiceResult<PublicAccess>> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || await shares.FindLinkByTokenAsync(token, ct) is not { } link)
        {
            return Fail<PublicAccess>(ErrorCode.NotFound, "Link unbekannt.");
        }

        if (link.ExpiresAt is { } expires && expires <= clock.GetUtcNow())
        {
            return Fail<PublicAccess>(ErrorCode.LinkExpired, "Der Link ist abgelaufen.");
        }

        if (!await policies.KindEnabledAsync(link.Kind, ct))
        {
            return Fail<PublicAccess>(ErrorCode.NotFound, "Diese Art von Link ist abgeschaltet."); // Richtlinie wirkt sofort, auch auf verschickte Links
        }

        var target = await spaces.GetNodeByIdAsync(link.SpaceId, link.NodeId, ct);
        if (!target.IsSuccess)
        {
            return Fail<PublicAccess>(ErrorCode.NotFound, "Das geteilte Element gibt es nicht mehr.");
        }

        var role = link.Kind == LinkKinds.Request ? SpaceRole.Editor : SpaceRole.Viewer;
        var view = await sharing.OpenNodeAsync(link.Id, link.SpaceId, link.NodeId, role, role, ct);
        return view.IsSuccess
            ? ServiceResult.Ok(new PublicAccess(link, target.Value!, view.Value!, await accounts.UserNameOfAsync(link.CreatedBy, ct)))
            : ServiceResult.Fail<PublicAccess>(view.Error!);
    }

    public static PublicLinkInfo Describe(PublicAccess access, bool unlocked) => new(
        access.Link.Kind,
        NameOf(access.Target.Path),
        access.Target.Kind,
        access.Target.Size,
        access.Target.MTime,
        access.Owner,
        access.Link.PasswordHash is not null && !unlocked,
        access.Link.ExpiresAt);

    public bool VerifyPassword(PublicAccess access, string? password) =>
        access.Link.PasswordHash is not { } hash || (!string.IsNullOrEmpty(password) && hasher.Verify(password, hash));

    /// <summary>Datei zum Herunterladen: bei einem Datei-Link das Ziel selbst, bei einem Ordner-Link eine Datei darin. Nicht bei Dateianfragen.</summary>
    public async Task<ServiceResult<NodeDetail>> OpenFileAsync(PublicAccess access, string? path, CancellationToken ct)
    {
        if (access.Link.Kind != LinkKinds.Share)
        {
            return Fail<NodeDetail>(ErrorCode.Forbidden, "Eine Dateianfrage zeigt keine Inhalte.");
        }

        var node = access.IsFolder
            ? await access.View.GetNodeAsync(path ?? string.Empty, ct)
            : await spaces.GetNodeAsync(access.Link.SpaceId, access.Target.Path, ct);
        return node.IsSuccess && (node.Value!.Node.Deleted || node.Value.Node.Kind != NodeKinds.File)
            ? Fail<NodeDetail>(ErrorCode.NotFound, "Keine Datei.")
            : node;
    }

    /// <summary>Inhalt eines Ordners unter dem Link (nur Ordner-Links zum Ansehen).</summary>
    public static async Task<ServiceResult<IReadOnlyList<SnapshotEntry>>> ListAsync(PublicAccess access, string? path, CancellationToken ct) =>
        access.Link.Kind == LinkKinds.Share && access.IsFolder
            ? await access.View.GetSnapshotAsync(path ?? string.Empty, ct, depth: 1)
            : Fail<IReadOnlyList<SnapshotEntry>>(ErrorCode.Forbidden, "Hier gibt es nichts aufzulisten.");

    public static async Task<ServiceResult<IReadOnlyList<ArchiveEntry>>> PlanArchiveAsync(PublicAccess access, IReadOnlyList<string> paths, CancellationToken ct) =>
        access.Link.Kind == LinkKinds.Share && access.IsFolder
            ? await access.View.PlanArchiveAsync(paths, ct)
            : Fail<IReadOnlyList<ArchiveEntry>>(ErrorCode.Forbidden, "Kein Ordner zum Herunterladen.");

    /// <summary>
    /// Dateianfrage: legt die hochgeladene Datei im Ordner ab, ohne Vorhandenes zu überschreiben („Bericht (2).pdf“). Der Server zerlegt den
    /// Strom selbst in Blöcke; <paramref name="maxBytes"/> begrenzt die Größe. Als Autor gilt, wer die Anfrage angelegt hat.
    /// </summary>
    public async Task<ServiceResult<PublicUploadResponse>> UploadAsync(
        PublicAccess access, string? fileName, Stream content, long maxBytes, CancellationToken ct)
    {
        if (!IsRequest(access))
        {
            return Fail<PublicUploadResponse>(ErrorCode.Forbidden, NoUploads);
        }

        if (ValidName(fileName) is not { } name)
        {
            return Fail<PublicUploadResponse>(ErrorCode.InvalidPath, "Ungültiger Dateiname.");
        }

        long received = 0;
        var tooLarge = false;
        ServiceError? blockError = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ChunkedFile chunked;
        try
        {
            chunked = await FastCdcChunker.ChunkAsync(content, new ChunkerOptions(), async (chunk, data, token) =>
            {
                received += chunk.Length;
                if (received > maxBytes)
                {
                    tooLarge = true;
                    await limit.CancelAsync();
                    token.ThrowIfCancellationRequested();
                }

                using var block = new MemoryStream(data.ToArray(), writable: false);
                if (await spaces.PutBlockAsync(chunk.Hash.Value, block, access.Link.CreatedBy, token) is { IsSuccess: false } failed)
                {
                    blockError ??= failed.Error;
                }
            }, limit.Token);
        }
        catch (OperationCanceledException) when (tooLarge)
        {
            return Fail<PublicUploadResponse>(ErrorCode.InvalidRequest, "Die Datei ist zu groß.");
        }

        if (blockError is not null)
        {
            return ServiceResult.Fail<PublicUploadResponse>(blockError);
        }

        return await StoreUniqueAsync(
            access, name, [.. chunked.Chunks.Select(c => c.Hash.Value)], chunked.Size, chunked.FileHash.Value, ct);
    }

    /// <summary>Dateianfrage aus dem Browser: welche Blöcke fehlen noch? (Der Browser zerlegt selbst und lädt nur Fehlendes.)</summary>
    public async Task<ServiceResult<MissingBlocksResponse>> MissingBlocksAsync(PublicAccess access, IReadOnlyList<string> hashes, CancellationToken ct) =>
        IsRequest(access) ? await spaces.MissingBlocksAsync(access.Link.SpaceId, hashes, access.Link.CreatedBy, ct) : Fail<MissingBlocksResponse>(ErrorCode.Forbidden, NoUploads);

    public async Task<ServiceResult<bool>> PutBlockAsync(PublicAccess access, string hash, Stream content, CancellationToken ct) =>
        IsRequest(access) ? await spaces.PutBlockAsync(hash, content, access.Link.CreatedBy, ct) : Fail<bool>(ErrorCode.Forbidden, NoUploads);

    /// <summary>Dateianfrage aus dem Browser: legt die Datei aus schon hochgeladenen Blöcken an, ohne Vorhandenes zu überschreiben.</summary>
    public async Task<ServiceResult<PublicUploadResponse>> CommitUploadAsync(
        PublicAccess access, PublicFileRequest request, long maxBytes, CancellationToken ct)
    {
        if (!IsRequest(access))
        {
            return Fail<PublicUploadResponse>(ErrorCode.Forbidden, NoUploads);
        }

        if (ValidName(request.Name) is not { } name)
        {
            return Fail<PublicUploadResponse>(ErrorCode.InvalidPath, "Ungültiger Dateiname.");
        }

        return request.Size > maxBytes
            ? Fail<PublicUploadResponse>(ErrorCode.InvalidRequest, "Die Datei ist zu groß.")
            : await StoreUniqueAsync(access, name, request.Blocks ?? [], request.Size, request.FileHash, ct);
    }

    private async Task<ServiceResult<PublicUploadResponse>> StoreUniqueAsync(
        PublicAccess access, string name, IReadOnlyList<string> blocks, long size, string fileHash, CancellationToken ct)
    {
        var existing = await access.View.GetSnapshotAsync(string.Empty, ct, depth: 1);
        var taken = new HashSet<string>(existing.Value?.Select(e => e.Path) ?? [], StringComparer.OrdinalIgnoreCase);
        for (var attempt = 1; attempt <= MaxUniqueNameAttempts; attempt++)
        {
            var candidate = attempt == 1 ? name : Numbered(name, attempt);
            if (taken.Contains(candidate))
            {
                continue;
            }

            var put = await access.View.CommitAsync(
                new CommitRequest(CommitOps.Put, candidate, 0, Blocks: blocks, Size: size, MTime: clock.GetUtcNow(), FileHash: fileHash),
                idempotencyKey: null, access.Link.CreatedBy, ct);
            if (put.IsSuccess)
            {
                return ServiceResult.Ok(new PublicUploadResponse(candidate, size));
            }

            if (put.Error!.Code is not (ErrorCode.AlreadyExists or ErrorCode.RevisionConflict))
            {
                return ServiceResult.Fail<PublicUploadResponse>(put.Error);
            }

            taken.Add(candidate); // inzwischen belegt: nächster Name
        }

        return Fail<PublicUploadResponse>(ErrorCode.AlreadyExists, "Kein freier Name.");
    }

    private static bool IsRequest(PublicAccess access) => access.Link.Kind == LinkKinds.Request && access.IsFolder;

    private static string? ValidName(string? fileName)
    {
        var name = fileName?.Trim() ?? string.Empty;
        return name.Length > 0 && !name.Contains('/') && RelativePath.TryParse(name, out _) ? name : null;
    }

    /// <summary>„Bericht.pdf“ → „Bericht (2).pdf“.</summary>
    internal static string Numbered(string name, int number)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({number}){name[dot..]}" : $"{name} ({number})";
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static ServiceResult<T> Fail<T>(string code, string message) => ServiceResult.Fail<T>(new ServiceError(code, message));
}
