using System.Security.Cryptography;
using System.Text;
using SyncDrive.Server.App.Spaces;

namespace SyncDrive.Server.App.Accounts;

/// <summary>
/// Echtzeit für die Weboberfläche: ein Stand je Benutzer über alles, was er sieht – seine Spaces, die mit ihm geteilten Ordner und ein eigenes
/// Signal für Benachrichtigungen, Favoriten, Labels und Freigaben. Ändert sich etwas davon, wacht das Warten auf; die Oberfläche lädt dann
/// die offene Ansicht neu. Der Stand ist undurchsichtig (Hash), ändert sich auch, wenn ein Space dazukommt oder wegfällt, und nach einem
/// Neustart des Servers.
/// </summary>
public sealed class AccountEvents(IChangeNotifier notifier)
{
    private readonly string _instance = Guid.NewGuid().ToString("N");

    /// <summary>Etwas Persönliches des Benutzers hat sich geändert (nicht der Inhalt eines Spaces; den meldet der Space selbst).</summary>
    public void Touch(string userId) => notifier.Notify(Key(userId));

    /// <summary>
    /// Kehrt sofort zurück, wenn <paramref name="after"/> nicht dem aktuellen Stand entspricht, sonst bei der nächsten Änderung oder nach
    /// <paramref name="timeout"/>. Ergebnis ist der neue Stand.
    /// </summary>
    public async Task<string> WaitAsync(
        string userId, IEnumerable<string> spaceIds, string? after, TimeSpan timeout, CancellationToken ct)
    {
        string[] keys = [Key(userId), .. spaceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var seen = Array.ConvertAll(keys, notifier.CurrentVersion);
        var stamp = Stamp(keys, seen);
        if (after != stamp)
        {
            return stamp;
        }

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waiting.CancelAfter(timeout);
        try
        {
            await await Task.WhenAny(keys.Select((key, i) => notifier.WaitAsync(key, seen[i], waiting.Token)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Zeit abgelaufen: unveränderter Stand
        }
        finally
        {
            await waiting.CancelAsync(); // die übrigen Wartenden freigeben
        }

        return Stamp(keys, Array.ConvertAll(keys, notifier.CurrentVersion));
    }

    private static string Key(string userId) => "user:" + userId;

    private string Stamp(string[] keys, long[] versions)
    {
        var text = new StringBuilder(_instance);
        for (var i = 0; i < keys.Length; i++)
        {
            text.Append(';').Append(keys[i]).Append('=').Append(versions[i]);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))[..16]);
    }
}
