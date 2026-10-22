using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Server.App.Ports;

namespace SyncDrive.Server.App.Spaces;

/// <summary>
/// Blöcke (inhaltsadressiert, einmal gespeichert für alle Spaces). Seit 0.15.0 je Space abgeschirmt: Herunterladen und „fehlt?“ beantwortet
/// ein Space nur für Blöcke, die eine seiner Dateien oder Versionen verwendet; ein Commit nimmt nur solche Blöcke oder selbst hochgeladene.
/// Wer bloß den Hash einer fremden Datei kennt, erfährt so weder, ob es sie gibt, noch kommt er an ihren Inhalt. Preis: dieselbe Datei in
/// zwei Spaces wird zweimal übertragen; gespeichert wird sie weiterhin nur einmal.
/// </summary>
public sealed partial class SpaceService
{
    private readonly RecentUploads _uploads = new(clock ?? TimeProvider.System);

    /// <summary>Ein Block dieses Space; fremde gibt es für den Aufrufer nicht (404, wie unbekannte).</summary>
    public async Task<ServiceResult<Stream>> OpenBlockAsync(string spaceId, string hash, CancellationToken ct)
    {
        if (!ContentHash.TryParse(hash, out var parsed))
        {
            return Fail<Stream>(ErrorCode.InvalidRequest, "Ungültiger Hash.");
        }

        return await ReferencedAsync(spaceId, [parsed], ct) is { Count: 1 }
            ? await OpenStoredBlockAsync(hash, ct)
            : Fail<Stream>(ErrorCode.NotFound, "Block unbekannt.");
    }

    /// <summary>
    /// Ein gespeicherter Block ohne Prüfung des Space: nur für Blöcke eines Knotens oder einer Version, deren Zugriff der Aufrufer schon
    /// geprüft hat (Herunterladen als Strom, ZIP).
    /// </summary>
    public async Task<ServiceResult<Stream>> OpenStoredBlockAsync(string hash, CancellationToken ct)
    {
        if (!ContentHash.TryParse(hash, out var parsed))
        {
            return Fail<Stream>(ErrorCode.InvalidRequest, "Ungültiger Hash.");
        }

        return await blocks.OpenReadAsync(parsed, ct) is { } stream
            ? ServiceResult.Ok(stream)
            : Fail<Stream>(ErrorCode.NotFound, "Block unbekannt.");
    }

    /// <summary>Welche Blöcke <paramref name="uploaderId"/> für einen Commit in diesem Space noch hochladen muss.</summary>
    public async Task<ServiceResult<MissingBlocksResponse>> MissingBlocksAsync(
        string spaceId, IEnumerable<string> hashes, string? uploaderId, CancellationToken ct)
    {
        var parsed = new List<ContentHash>();
        foreach (var text in hashes)
        {
            if (!ContentHash.TryParse(text, out var hash))
            {
                return Fail<MissingBlocksResponse>(ErrorCode.InvalidRequest, $"Ungültiger Hash: '{text}'.");
            }

            parsed.Add(hash);
        }

        if (!await store.SpaceExistsAsync(spaceId, ct))
        {
            return Fail<MissingBlocksResponse>(ErrorCode.SpaceNotFound, "Space unbekannt.");
        }

        var missing = await FindMissingAsync(spaceId, parsed, uploaderId, ct);
        return ServiceResult.Ok(new MissingBlocksResponse([.. missing.Select(h => h.Value)]));
    }

    /// <summary>Speichert einen Block (Hash geprüft) und merkt sich, wer ihn hochgeladen hat.</summary>
    public async Task<ServiceResult<bool>> PutBlockAsync(string hash, Stream content, string? uploaderId, CancellationToken ct)
    {
        if (!ContentHash.TryParse(hash, out var parsed))
        {
            return Fail<bool>(ErrorCode.InvalidRequest, "Ungültiger Hash.");
        }

        if (await blocks.WriteAsync(parsed, content, ct) != BlockWriteResult.Stored)
        {
            return Fail<bool>(ErrorCode.BlockHashMismatch, "Inhalt passt nicht zum Hash.");
        }

        if (uploaderId is not null)
        {
            _uploads.Record(uploaderId, parsed);
        }

        return ServiceResult.Ok(true);
    }

    /// <summary>
    /// Fehlend ist ein Block, den der Speicher nicht hat – oder (mit <paramref name="uploaderId"/>) einer, den weder der Space verwendet noch
    /// der Handelnde hochgeladen hat. Ohne <paramref name="uploaderId"/> (Systemvorgänge) zählt nur der Speicher.
    /// </summary>
    private async Task<List<ContentHash>> FindMissingAsync(string spaceId, IEnumerable<ContentHash> hashes, string? uploaderId, CancellationToken ct)
    {
        var distinct = hashes.Distinct().ToList();
        var referenced = uploaderId is null ? null : await ReferencedAsync(spaceId, distinct, ct);
        var missing = new List<ContentHash>();
        foreach (var hash in distinct)
        {
            var allowed = referenced is null || referenced.Contains(hash) || _uploads.UploadedBy(uploaderId!, hash);
            if (!allowed || await blocks.GetSizeAsync(hash, ct) is null)
            {
                missing.Add(hash);
            }
        }

        return missing;
    }

    private async Task<IReadOnlySet<ContentHash>> ReferencedAsync(string spaceId, List<ContentHash> hashes, CancellationToken ct) =>
        hashes.Count == 0 || !await store.SpaceExistsAsync(spaceId, ct)
            ? new HashSet<ContentHash>()
            : await store.InTransactionAsync(spaceId, tx => tx.FindReferencedBlocksAsync(hashes), ct);
}
