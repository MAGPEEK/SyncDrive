using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Infra.Blob;

/// <summary>
/// Profilbilder im Datenordner (<c>avatars/&lt;Benutzer-Id&gt;.jpg</c>): liegen im Volume und überstehen Container-Updates. Geschrieben wird
/// über eine Temp-Datei und Umbenennen, damit nie ein halbes Bild ausgeliefert wird.
/// </summary>
public sealed class FileAvatarStore(string directory) : IAvatarStore
{
    public async Task SaveAsync(string userId, byte[] jpeg, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = PathOf(userId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, jpeg, ct);
        File.Move(temp, path, overwrite: true);
    }

    public Task<Stream?> OpenAsync(string userId, CancellationToken ct) =>
        Task.FromResult<Stream?>(File.Exists(PathOf(userId)) ? File.OpenRead(PathOf(userId)) : null);

    public Task DeleteAsync(string userId, CancellationToken ct)
    {
        File.Delete(PathOf(userId));
        return Task.CompletedTask;
    }

    /// <summary>Benutzer-Ids sind „u_“ plus Hex; alles andere gelangt nie in einen Dateinamen.</summary>
    private string PathOf(string userId) =>
        userId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? Path.Combine(directory, userId + ".jpg")
            : throw new ArgumentException("Ungültige Benutzer-Id.", nameof(userId));
}
