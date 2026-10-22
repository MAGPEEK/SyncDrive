using System.Text;
using SyncDrive.Infra.Secrets;

namespace SyncDrive.Client.Tests;

/// <summary>DPAPI gibt es nur unter Windows; unter Linux enden die Tests sofort (der Client läuft vorerst nur unter Windows).</summary>
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "syncdrive-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_secret_round_trips_can_be_replaced_and_deleted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DpapiCredentialStore(_directory);
        Assert.Null(await store.GetAsync("connection/c1/device-token", default));

        await store.SaveAsync("connection/c1/device-token", "token-1", default);
        await store.SaveAsync("connection/c1/device-token", "token-2", default);
        await store.SaveAsync("connection/c2/device-token", "anderer", default);

        Assert.Equal("token-2", await new DpapiCredentialStore(_directory).GetAsync("connection/c1/device-token", default));
        Assert.Equal("anderer", await store.GetAsync("connection/c2/device-token", default));

        await store.DeleteAsync("connection/c1/device-token", default);
        await store.DeleteAsync("gibt-es-nicht", default);
        Assert.Null(await store.GetAsync("connection/c1/device-token", default));
    }

    [Fact]
    public async Task Neither_secret_nor_key_appear_in_the_files()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DpapiCredentialStore(_directory);
        await store.SaveAsync("connection/mein-nas/device-token", "streng-geheimer-token", default);

        var file = Assert.Single(Directory.GetFiles(_directory));
        Assert.DoesNotContain("mein-nas", Path.GetFileName(file), StringComparison.Ordinal);
        Assert.DoesNotContain("streng-geheimer-token", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_damaged_file_counts_as_not_signed_in()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DpapiCredentialStore(_directory);
        await store.SaveAsync("k", "v", default);
        await File.WriteAllBytesAsync(Assert.Single(Directory.GetFiles(_directory)), [1, 2, 3]);

        Assert.Null(await store.GetAsync("k", default));
    }
}
