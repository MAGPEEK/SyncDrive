using System.Security.Cryptography;
using SyncDrive.Core.Chunking;
using SyncDrive.Core.Model;

namespace SyncDrive.Core.Tests;

public class FastCdcChunkerTests
{
    private static readonly ChunkerOptions Small = new(MinSize: 64, AverageSize: 256, MaxSize: 1024);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static Task<ChunkedFile> Chunk(byte[] data, ChunkerOptions? options = null) =>
        FastCdcChunker.ChunkAsync(new MemoryStream(data), options ?? Small, Ct);

    [Fact]
    public async Task Empty_input_has_no_chunks_and_the_hash_of_nothing()
    {
        var result = await Chunk([]);

        Assert.Empty(result.Chunks);
        Assert.Equal(0, result.Size);
        Assert.Equal(ContentHash.FromBytes(SHA256.HashData([])), result.FileHash);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public async Task Input_up_to_the_minimum_size_is_one_chunk(int length)
    {
        var data = RandomBytes(length, 1);

        var result = await Chunk(data);

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal(length, chunk.Length);
        Assert.Equal(result.FileHash, chunk.Hash);
    }

    [Fact]
    public async Task The_streaming_variant_hands_over_each_chunk_with_exactly_its_content()
    {
        var data = RandomBytes(50_000, 7);
        var handed = new List<(Chunk Chunk, byte[] Content)>();

        var result = await FastCdcChunker.ChunkAsync(
            new NonSeekableStream(data), Small, (chunk, content, _) =>
            {
                handed.Add((chunk, content.ToArray()));
                return Task.CompletedTask;
            },
            Ct);

        Assert.Equal(result.Chunks, handed.Select(h => h.Chunk));
        Assert.All(handed, h => Assert.Equal(data.AsSpan((int)h.Chunk.Offset, h.Chunk.Length).ToArray(), h.Content));
        Assert.Equal((await Chunk(data)).Chunks, result.Chunks); // dieselben Grenzen wie ohne Weitergabe
    }

    /// <summary>Wie eine Browser-Datei: nur vorwärts lesbar.</summary>
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    [Theory]
    [InlineData(1_000, 1)]
    [InlineData(50_000, 2)]
    [InlineData(333_333, 3)]
    public async Task Chunks_are_gapless_in_bounds_and_hash_to_the_whole_file(int length, int seed)
    {
        var data = RandomBytes(length, seed);

        var result = await Chunk(data);

        long expectedOffset = 0;
        foreach (var chunk in result.Chunks)
        {
            Assert.Equal(expectedOffset, chunk.Offset);
            Assert.InRange(chunk.Length, 1, Small.MaxSize);
            Assert.Equal(ContentHash.FromBytes(SHA256.HashData(data.AsSpan((int)chunk.Offset, chunk.Length))), chunk.Hash);
            expectedOffset += chunk.Length;
        }

        Assert.Equal(length, expectedOffset);
        Assert.Equal(length, result.Size);
        Assert.Equal(ContentHash.FromBytes(SHA256.HashData(data)), result.FileHash);
        Assert.All(result.Chunks.SkipLast(1), c => Assert.True(c.Length >= Small.MinSize));
    }

    [Fact]
    public async Task Average_chunk_size_is_near_the_target()
    {
        var result = await Chunk(RandomBytes(2_000_000, 7));

        var average = result.Size / (double)result.Chunks.Count;

        Assert.InRange(average, Small.AverageSize * 0.6, Small.AverageSize * 1.6);
    }

    [Fact]
    public async Task Chunking_is_deterministic()
    {
        var data = RandomBytes(100_000, 4);

        var first = await Chunk(data);
        var second = await Chunk(data);

        Assert.Equal(first.Chunks, second.Chunks);
    }

    [Fact]
    public async Task Result_does_not_depend_on_how_the_stream_delivers_bytes()
    {
        var data = RandomBytes(40_000, 5);

        var whole = await Chunk(data);
        var trickle = await FastCdcChunker.ChunkAsync(new TrickleStream(data, 7), Small, Ct);

        Assert.Equal(whole.Chunks, trickle.Chunks);
        Assert.Equal(whole.FileHash, trickle.FileHash);
    }

    [Fact]
    public async Task Inserting_bytes_early_keeps_nearly_all_later_chunks()
    {
        var original = RandomBytes(200_000, 6);
        var edited = original.Take(1000).Concat(Enumerable.Repeat((byte)0xAB, 37)).Concat(original.Skip(1000)).ToArray();

        var before = (await Chunk(original)).Chunks.Select(c => c.Hash).ToHashSet();
        var after = (await Chunk(edited)).Chunks;

        var reused = after.Count(c => before.Contains(c.Hash));
        Assert.True(reused >= after.Count - 4, $"{reused} von {after.Count} Blöcken blieben gleich");
    }

    [Fact]
    public async Task Repeated_content_yields_repeated_chunk_hashes()
    {
        var block = RandomBytes(5_000, 8);
        var data = block.Concat(block).Concat(block).ToArray();

        var result = await Chunk(data);

        Assert.True(result.Chunks.Select(c => c.Hash).Distinct().Count() < result.Chunks.Count);
    }

    [Fact]
    public async Task Default_sizes_work_on_a_multi_megabyte_file()
    {
        var data = RandomBytes(10 * 1024 * 1024, 9);
        var options = new ChunkerOptions();

        var result = await Chunk(data, options);

        Assert.Equal(data.Length, result.Size);
        Assert.InRange(result.Chunks.Count, 4, 60);
        Assert.All(result.Chunks.SkipLast(1), c => Assert.InRange(c.Length, options.MinSize, options.MaxSize));
    }

    [Theory]
    [InlineData(0, 256, 1024)]
    [InlineData(256, 256, 1024)]
    [InlineData(64, 300, 1024)]
    [InlineData(64, 1024, 1024)]
    public void Invalid_options_are_rejected(int min, int average, int max)
    {
        Assert.Throws<ArgumentException>(() => new ChunkerOptions(min, average, max).Validate());
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FastCdcChunker.ChunkAsync(new MemoryStream(RandomBytes(10_000, 1)), Small, cts.Token));
    }

    /// <summary>Liefert höchstens <c>step</c> Bytes je Lesevorgang, wie ein langsames Netz oder eine Pipe.</summary>
    private sealed class TrickleStream(byte[] data, int step) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, step), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
