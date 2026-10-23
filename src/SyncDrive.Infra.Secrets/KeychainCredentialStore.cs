using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SyncDrive.Client.App.Ports;

namespace SyncDrive.Infra.Secrets;

/// <summary>
/// Geheimnisse (Geräte-Token, Proxy-Passwort) im Schlüsselbund des macOS-Benutzers, als generische Passwörter des Dienstes „SyncDrive“
/// (ADR-14). Wie unter Windows (<see cref="DpapiCredentialStore"/>) ist der Kontoname ein Hash des Schlüssels, damit der Schlüsselbund
/// nichts über Verbindungen verrät; die Konfigurationsdatenbank enthält die Geheimnisse nie.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class KeychainCredentialStore(string service = "SyncDrive") : ICredentialStore
{
    /// <summary>
    /// Gelesene Geheimnisse bleiben im Speicher des Daemons, der sie ohnehin benutzt: jede Anfrage an den Schlüsselbund ist ein Aufruf an
    /// <c>securityd</c>. Nur dieser Prozess schreibt die Einträge, Speichern und Löschen halten den Zwischenspeicher aktuell.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public Task SaveAsync(string key, string secret, CancellationToken ct)
    {
        _cache.TryRemove(key, out _);
        using var query = Query(key);
        using var data = CoreFoundation.Data(Encoding.UTF8.GetBytes(secret));
        using var update = CoreFoundation.Dictionary();
        update.Set(Keychain.ValueData, data);
        var status = Keychain.SecItemUpdate(query.Handle, update.Handle);
        if (status == Keychain.ItemNotFound)
        {
            query.Set(Keychain.ValueData, data);
            status = Keychain.SecItemAdd(query.Handle, IntPtr.Zero);
        }

        Keychain.Check(status, "speichern");
        _cache[key] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult<string?>(cached);
        }

        using var query = Query(key);
        query.Set(Keychain.ReturnData, CoreFoundation.True);
        query.Set(Keychain.MatchLimit, Keychain.MatchLimitOne);
        var status = Keychain.SecItemCopyMatching(query.Handle, out var result);
        if (status == Keychain.ItemNotFound)
        {
            return Task.FromResult<string?>(null);
        }

        Keychain.Check(status, "lesen");
        using var data = new CoreFoundation.Owned(result);
        var secret = Encoding.UTF8.GetString(CoreFoundation.Bytes(data.Handle));
        _cache[key] = secret;
        return Task.FromResult<string?>(secret);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        _cache.TryRemove(key, out _);
        using var query = Query(key);
        var status = Keychain.SecItemDelete(query.Handle);
        if (status != Keychain.ItemNotFound)
        {
            Keychain.Check(status, "löschen");
        }

        return Task.CompletedTask;
    }

    private CoreFoundation.MutableDictionary Query(string key)
    {
        var query = CoreFoundation.Dictionary();
        query.Set(Keychain.Class, Keychain.ClassGenericPassword);
        using var serviceName = CoreFoundation.String(service);
        using var account = CoreFoundation.String(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        query.Set(Keychain.AttrService, serviceName);
        query.Set(Keychain.AttrAccount, account);
        return query;
    }

    /// <summary>Security-Framework: <c>SecItem…</c> und die Schlüssel der Abfragen (exportierte <c>CFStringRef</c>-Konstanten).</summary>
    private static class Keychain
    {
        public const int ItemNotFound = -25300; // errSecItemNotFound
        private const string Library = "/System/Library/Frameworks/Security.framework/Security";

        private static readonly IntPtr Handle = NativeLibrary.Load(Library);

        public static IntPtr Class { get; } = Constant("kSecClass");

        public static IntPtr ClassGenericPassword { get; } = Constant("kSecClassGenericPassword");

        public static IntPtr AttrService { get; } = Constant("kSecAttrService");

        public static IntPtr AttrAccount { get; } = Constant("kSecAttrAccount");

        public static IntPtr ValueData { get; } = Constant("kSecValueData");

        public static IntPtr ReturnData { get; } = Constant("kSecReturnData");

        public static IntPtr MatchLimit { get; } = Constant("kSecMatchLimit");

        public static IntPtr MatchLimitOne { get; } = Constant("kSecMatchLimitOne");

        public static void Check(int status, string what)
        {
            if (status != 0)
            {
                throw new IOException($"Schlüsselbund: {what} fehlgeschlagen (OSStatus {status}).");
            }
        }

        [DllImport(Library)]
        public static extern int SecItemAdd(IntPtr attributes, IntPtr result);

        [DllImport(Library)]
        public static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

        [DllImport(Library)]
        public static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [DllImport(Library)]
        public static extern int SecItemDelete(IntPtr query);

        private static IntPtr Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Handle, name));
    }
}
