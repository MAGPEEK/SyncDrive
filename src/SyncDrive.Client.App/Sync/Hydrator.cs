using System.Security.Cryptography;
using SyncDrive.Client.App.Ports;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Sync;

/// <summary>
/// Liefert den Inhalt eines Platzhalters, wenn ein Programm ihn öffnet (On-Demand). Er kommt Block für Block vom Server; jeder Block wird
/// geprüft, bevor er weitergeht, denn Geliefertes lässt sich nicht zurücknehmen. Passt die Serverfassung nicht mehr zum Platzhalter, scheitert
/// das Öffnen: Der Sync erneuert den Platzhalter gleich, danach klappt es.
/// </summary>
public sealed class Hydrator(IRemoteApi remote)
{
    /// <summary>
    /// Schreibt den Inhalt der Fassung <paramref name="expected"/> der Reihe nach in <paramref name="sink"/>; Ergebnis: gelieferte Bytes.
    /// Ohne gemerkte Fassung (eine früher gewöhnliche Datei, die Windows später freigegeben hat) genügt eine Serverfassung mit der Größe des
    /// Platzhalters; jeder Block wird trotzdem geprüft.
    /// </summary>
    public async Task<Result<long>> CopyToAsync(
        RelativePath path, ContentHash? expected, long expectedSize, Func<ReadOnlyMemory<byte>, CancellationToken, Task> sink, CancellationToken ct)
    {
        var found = await remote.GetNodeAsync(path, ct);
        if (!found.IsSuccess)
        {
            return Result.Fail<long>(found.Error!);
        }

        if (found.Value is not { State: { Kind: NodeKind.File } state } node || state.Size != expectedSize
            || (expected is not null && state.Hash != expected))
        {
            return Result.Fail<long>(SyncErrorKind.Transient, "placeholder_stale", "Die Datei hat sich auf dem Server geändert.");
        }

        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (var hash in node.Blocks)
        {
            var download = await remote.DownloadBlockAsync(hash, ct);
            if (!download.IsSuccess)
            {
                return Result.Fail<long>(download.Error!);
            }

            byte[] bytes;
            await using (var block = download.Value!)
            {
                using var buffer = new MemoryStream();
                await block.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }

            if (ContentHash.FromBytes(SHA256.HashData(bytes)) != hash)
            {
                return Result.Fail<long>(SyncErrorKind.Transient, "block_corrupt", $"Block {hash} kam beschädigt an.");
            }

            fileHash.AppendData(bytes);
            await sink(bytes, ct);
            total += bytes.Length;
        }

        if (total != state.Size || (state.Hash is { } want && ContentHash.FromBytes(fileHash.GetHashAndReset()) != want))
        {
            return Result.Fail<long>(SyncErrorKind.Transient, "file_mismatch", "Inhalt passt nicht zur Serverfassung.");
        }

        return Result.Ok(total);
    }
}
