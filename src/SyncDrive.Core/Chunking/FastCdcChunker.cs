using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using SyncDrive.Core.Model;

namespace SyncDrive.Core.Chunking;

/// <param name="MinSize">Kein Block ist kleiner (außer dem letzten).</param>
/// <param name="AverageSize">Zielgröße; muss eine Zweierpotenz sein.</param>
/// <param name="MaxSize">Kein Block ist größer.</param>
public sealed record ChunkerOptions(int MinSize = 256 * 1024, int AverageSize = 1024 * 1024, int MaxSize = 4 * 1024 * 1024)
{
    public void Validate()
    {
        if (MinSize < 1 || MinSize >= AverageSize || AverageSize >= MaxSize)
        {
            throw new ArgumentException("Es muss gelten: 0 < Min < Durchschnitt < Max.");
        }

        if (!BitOperations.IsPow2(AverageSize) || AverageSize < 64)
        {
            throw new ArgumentException("Die Durchschnittsgröße muss eine Zweierpotenz >= 64 sein.");
        }
    }
}

public readonly record struct Chunk(long Offset, int Length, ContentHash Hash);

/// <param name="FileHash">SHA-256 über den gesamten Inhalt.</param>
public sealed record ChunkedFile(long Size, ContentHash FileHash, IReadOnlyList<Chunk> Chunks);

/// <summary>
/// Inhaltsabhängige Blockgrenzen nach dem FastCDC-Verfahren (normalisiert, Stufe 2). Eine Einfügung mitten in einer
/// Datei verschiebt nur die Grenzen in ihrer Nähe; die Folgeblöcke bleiben gleich und müssen nicht neu übertragen werden.
/// Die Gear-Tabelle ist aus einem festen Startwert abgeleitet: Client und Server zerlegen identisch und dauerhaft.
/// </summary>
public static class FastCdcChunker
{
    private const int NormalizationLevel = 2;
    private static readonly ulong[] Gear = BuildGearTable();

    public static Task<ChunkedFile> ChunkAsync(Stream content, ChunkerOptions options, CancellationToken ct) =>
        ChunkAsync(content, options, null, ct);

    /// <summary>
    /// Wie oben, gibt aber jeden Block samt Inhalt sofort an <paramref name="onChunk"/> weiter, bevor der Puffer weiterrückt. So lässt sich
    /// ein nicht zurückspulbarer Strom (Browser-Datei) in einem Durchgang zerlegen und hochladen, mit höchstens zwei Blöcken im Speicher.
    /// </summary>
    public static async Task<ChunkedFile> ChunkAsync(
        Stream content, ChunkerOptions options, Func<Chunk, ReadOnlyMemory<byte>, CancellationToken, Task>? onChunk, CancellationToken ct)
    {
        options.Validate();
        var bits = BitOperations.Log2((uint)options.AverageSize);
        var maskSmall = MaskOfBits(bits + NormalizationLevel); // strenger: selten früh schneiden
        var maskLarge = MaskOfBits(bits - NormalizationLevel); // lockerer: nach dem Zielpunkt eher schneiden

        var buffer = ArrayPool<byte>.Shared.Rent(options.MaxSize * 2);
        try
        {
            using var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunks = new List<Chunk>();
            var filled = 0;
            var offset = 0L;
            var atEnd = false;
            while (true)
            {
                while (!atEnd && filled < options.MaxSize)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), ct);
                    atEnd = read == 0;
                    filled += read;
                }

                if (filled == 0)
                {
                    break;
                }

                var length = FindCut(buffer.AsSpan(0, filled), options, maskSmall, maskLarge);
                var slice = buffer.AsMemory(0, length);
                fileHasher.AppendData(slice.Span);
                var chunk = new Chunk(offset, length, ContentHash.FromBytes(SHA256.HashData(slice.Span)));
                chunks.Add(chunk);
                if (onChunk is not null)
                {
                    await onChunk(chunk, slice, ct);
                }

                offset += length;
                Buffer.BlockCopy(buffer, length, buffer, 0, filled - length);
                filled -= length;
            }

            return new ChunkedFile(offset, ContentHash.FromBytes(fileHasher.GetHashAndReset()), chunks);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Länge des ersten Blocks in <paramref name="data"/>; die Daten reichen mindestens bis MaxSize oder bis zum Dateiende.</summary>
    internal static int FindCut(ReadOnlySpan<byte> data, ChunkerOptions options, ulong maskSmall, ulong maskLarge)
    {
        var n = Math.Min(data.Length, options.MaxSize);
        if (n <= options.MinSize)
        {
            return n;
        }

        var normal = Math.Min(options.AverageSize, n);
        ulong fingerprint = 0;
        var i = options.MinSize;
        for (; i < normal; i++)
        {
            fingerprint = (fingerprint << 1) + Gear[data[i]];
            if ((fingerprint & maskSmall) == 0)
            {
                return i + 1;
            }
        }

        for (; i < n; i++)
        {
            fingerprint = (fingerprint << 1) + Gear[data[i]];
            if ((fingerprint & maskLarge) == 0)
            {
                return i + 1;
            }
        }

        return n;
    }

    internal static ulong MaskOfBits(int bits) => ((1UL << bits) - 1) << (64 - bits);

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        var state = 0x5EED_5EED_C0DE_0001UL; // fest: ändert man ihn, ändern sich alle Blockgrenzen
        for (var i = 0; i < table.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL; // SplitMix64
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }

        return table;
    }
}
