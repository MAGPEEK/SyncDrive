using SyncDrive.Core.Paths;
using SyncDrive.Infra.Fs;

namespace SyncDrive.Client.Tests;

public sealed class TrashRetentionTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncdrive-trash-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly LocalFileStore _store;

    public TrashRetentionTests()
    {
        Directory.CreateDirectory(_root);
        _store = new LocalFileStore(_root, _clock);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task TrashAsync(string name, int bytes)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, name), new byte[bytes], Ct);
        await _store.MoveToTrashAsync(RelativePath.Parse(name), Ct);
    }

    private int Batches() => Directory.GetDirectories(Path.Combine(_root, LocalFileStore.InternalFolder, "trash")).Length;

    [Fact]
    public async Task Batches_older_than_the_retention_are_removed_younger_ones_stay()
    {
        await TrashAsync("alt.txt", 10);
        _clock.Advance(TimeSpan.FromDays(20));
        await TrashAsync("jung.txt", 10);
        _clock.Advance(TimeSpan.FromDays(11)); // alt: 31 Tage, jung: 11 Tage

        var removed = await _store.CleanTrashAsync(TimeSpan.FromDays(30), long.MaxValue, Ct);

        Assert.Equal((1, 1), (removed, Batches()));
    }

    [Fact]
    public async Task When_over_the_size_limit_the_oldest_batches_go_first()
    {
        await TrashAsync("eins.bin", 1000);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await TrashAsync("zwei.bin", 1000);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await TrashAsync("drei.bin", 1000);

        var removed = await _store.CleanTrashAsync(TimeSpan.FromDays(30), 2000, Ct);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_root, LocalFileStore.InternalFolder, "trash"), "*", SearchOption.AllDirectories),
            f => f.EndsWith("eins.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_happens_within_limits()
    {
        await TrashAsync("x.txt", 10);

        Assert.Equal(0, await _store.CleanTrashAsync(TimeSpan.FromDays(30), 10_000, Ct));
        Assert.Equal(1, Batches());
    }

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
