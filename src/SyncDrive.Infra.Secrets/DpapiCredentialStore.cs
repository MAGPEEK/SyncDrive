using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SyncDrive.Client.App.Ports;

namespace SyncDrive.Infra.Secrets;

/// <summary>
/// Geheimnisse (Geräte-Token, Proxy-Passwort) als Dateien, verschlüsselt mit DPAPI für den angemeldeten Windows-Benutzer (ADR-7).
/// Ein anderer Benutzer oder ein anderer Rechner kann sie nicht entschlüsseln; die Konfigurationsdatenbank enthält sie nie.
/// Dateiname ist ein Hash des Schlüssels, damit auch die Namen nichts über Verbindungen verraten. Schreiben ist atomar.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ICredentialStore
{
    /// <summary>Zusätzliches Geheimnis der Anwendung: andere Programme desselben Benutzers entschlüsseln die Dateien nicht beiläufig.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SyncDrive.CredentialStore.v1");

    private readonly string _directory;

    public DpapiCredentialStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public async Task SaveAsync(string key, string secret, CancellationToken ct)
    {
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        var target = PathFor(key);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, protectedBytes, ct);
        File.Move(temp, target, overwrite: true);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(await File.ReadAllBytesAsync(path, ct), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return null; // anderer Benutzer/Rechner oder beschädigt: wie „nicht angemeldet“ behandeln
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    private string PathFor(string key) =>
        Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".secret");
}
