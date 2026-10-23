using SyncDrive.Client.Daemon;

namespace SyncDrive.Server.Host.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "syncdrive-single-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_second_instance_is_refused_until_the_first_ends()
    {
        var name = "SyncDrive.Test." + Guid.NewGuid().ToString("N");

        var first = SingleInstance.TryAcquire(name, _folder);
        var second = SingleInstance.TryAcquire(name, _folder);
        Assert.NotNull(first);
        Assert.Null(second);

        first!.Dispose();
        using var third = SingleInstance.TryAcquire(name, _folder); // auch eine liegengebliebene Sperrdatei hindert nicht
        Assert.NotNull(third);
    }
}
