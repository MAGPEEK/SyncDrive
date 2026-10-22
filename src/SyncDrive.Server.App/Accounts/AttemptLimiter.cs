using System.Collections.Concurrent;

namespace SyncDrive.Server.App.Accounts;

/// <summary>Ein Zähler mit Grenze, z. B. „Anmeldung: Benutzer und Adresse, 5 Fehlversuche“.</summary>
public readonly record struct AttemptKey(string Key, int Limit);

/// <summary>
/// Bremst das Durchprobieren von Passwörtern (M7, Härtung): Fehlversuche je Schlüssel in einem festen Zeitfenster; ist eine Grenze erreicht,
/// wird bis zum Ende des Fensters abgelehnt, bevor das Passwort überhaupt geprüft wird. Erfolg setzt die genannten Schlüssel zurück. Im
/// Speicher des Serverprozesses (eine Instanz, ADR-13); nach einem Neustart beginnt die Zählung neu.
/// </summary>
public sealed class AttemptLimiter(TimeProvider clock, TimeSpan window)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);

    private const int SweepAbove = 10_000;
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Until)> _counters = new(StringComparer.Ordinal);

    /// <summary>Wie lange noch gesperrt ist; null, wenn ein Versuch erlaubt ist.</summary>
    public TimeSpan? RetryAfter(IEnumerable<AttemptKey> keys)
    {
        var now = clock.GetUtcNow();
        TimeSpan? longest = null;
        foreach (var key in keys)
        {
            if (_counters.TryGetValue(key.Key, out var counter) && counter.Until > now && counter.Failures >= key.Limit)
            {
                var remaining = counter.Until - now;
                longest = longest is { } l && l > remaining ? l : remaining;
            }
        }

        return longest;
    }

    public void Failed(IEnumerable<AttemptKey> keys)
    {
        var now = clock.GetUtcNow();
        if (_counters.Count > SweepAbove)
        {
            Sweep(now);
        }

        foreach (var key in keys)
        {
            _counters.AddOrUpdate(
                key.Key,
                _ => (1, now + window),
                (_, counter) => counter.Until > now ? (counter.Failures + 1, counter.Until) : (1, now + window));
        }
    }

    public void Succeeded(IEnumerable<AttemptKey> keys)
    {
        foreach (var key in keys)
        {
            _counters.TryRemove(key.Key, out _);
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, counter) in _counters)
        {
            if (counter.Until <= now)
            {
                _counters.TryRemove(key, out _);
            }
        }
    }
}
