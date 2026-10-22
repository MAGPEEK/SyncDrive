using System.Text.Json;
using SyncDrive.Server.App.Portal;

namespace SyncDrive.Infra.Blob;

/// <summary>
/// Anmeldeseite im Datenordner (<c>portal/settings.json</c>, <c>portal/background</c>): liegt im Volume und übersteht damit Container-Updates.
/// Geschrieben wird über eine Temp-Datei und Umbenennen, damit nie eine halbe Datei ausgeliefert wird.
/// </summary>
public sealed class FilePortalStore(string directory) : IPortalStore
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private string SettingsPath => Path.Combine(directory, "settings.json");

    private string BackgroundPath => Path.Combine(directory, "background");

    public async Task<PortalSettings?> GetAsync(CancellationToken ct)
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(SettingsPath);
        return await JsonSerializer.DeserializeAsync<PortalSettings>(stream, Json, ct);
    }

    public Task SaveAsync(PortalSettings settings, CancellationToken ct) =>
        WriteAtomicallyAsync(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(settings, Json), ct);

    public Task SaveBackgroundAsync(byte[] image, CancellationToken ct) => WriteAtomicallyAsync(BackgroundPath, image, ct);

    public Task<Stream?> OpenBackgroundAsync(CancellationToken ct) =>
        Task.FromResult<Stream?>(File.Exists(BackgroundPath) ? File.OpenRead(BackgroundPath) : null);

    public Task DeleteBackgroundAsync(CancellationToken ct)
    {
        File.Delete(BackgroundPath);
        return Task.CompletedTask;
    }

    private async Task WriteAtomicallyAsync(string path, byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, path, overwrite: true);
    }
}
