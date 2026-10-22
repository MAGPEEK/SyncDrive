using System.IO.Compression;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.Host;

/// <summary>
/// Packt die geplanten Einträge beim Senden in ein ZIP, Block für Block aus dem Speicher: Nichts wird vorher zusammengesetzt, die Größe
/// des Archivs spielt für den Arbeitsspeicher keine Rolle.
/// </summary>
internal static class ArchiveWriter
{
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task WriteAsync(Stream body, IReadOnlyList<ArchiveEntry> entries, SpaceService spaces, CancellationToken ct)
    {
        using var output = new DrainingStream(body);
        var buffer = new byte[81920];
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Name, CompressionLevel.Fastest);
                zipEntry.LastWriteTime = entry.MTime < ZipEpoch ? ZipEpoch : entry.MTime; // ZIP kennt keine Zeiten vor 1980
                if (entry.IsFolder)
                {
                    continue;
                }

                using var target = zipEntry.Open(); // schreibt nur in die Zwischenablage
                foreach (var hash in entry.Blocks)
                {
                    var block = await spaces.OpenStoredBlockAsync(hash, ct);
                    await using var content = block.Value ?? throw new IOException($"Block {hash} fehlt.");
                    int read;
                    while ((read = await content.ReadAsync(buffer, ct)) > 0)
                    {
                        target.Write(buffer, 0, read);
                        await output.DrainAsync(ct);
                    }
                }
            }
        }

        await output.DrainAsync(ct); // Verzeichnis am Ende des Archivs
    }

    /// <summary>
    /// <see cref="ZipArchive"/> schreibt nur synchron, Kestrel nimmt nur asynchrone Schreibvorgänge an. Die Zwischenablage hält deshalb, was
    /// das Archiv schreibt, bis der Aufrufer sie asynchron leert; sie wird nie größer als ein gelesener Abschnitt plus Puffer von Deflate.
    /// </summary>
    private sealed class DrainingStream(Stream target) : Stream
    {
        private readonly MemoryStream _pending = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task DrainAsync(CancellationToken ct)
        {
            if (_pending.Length == 0)
            {
                return;
            }

            await target.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
            _pending.SetLength(0);
        }

        public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pending.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
