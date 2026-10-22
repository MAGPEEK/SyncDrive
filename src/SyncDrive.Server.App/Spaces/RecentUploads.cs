using System.Collections.Concurrent;
using SyncDrive.Core.Model;

namespace SyncDrive.Server.App.Spaces;

/// <summary>
/// Wer welchen Block kürzlich hochgeladen hat (M7, Härtung). Ein Commit darf nur Blöcke verwenden, die schon im Space vorkommen oder die der
/// Handelnde selbst hochgeladen hat; sonst könnte jemand mit dem bloßen Hash einer fremden Datei an deren Inhalt kommen. Hochladen und Commit
/// liegen dicht beieinander, deshalb genügt ein Gedächtnis im Serverprozess mit begrenzter Dauer und Größe (eine Instanz, ADR-13).
/// </summary>
internal sealed class RecentUploads(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaxEntries = 200_000;
    private readonly ConcurrentDictionary<(string User, ContentHash Hash), DateTimeOffset> _uploads = new();

    public void Record(string userId, ContentHash hash)
    {
        if (_uploads.Count >= MaxEntries)
        {
            Prune();
        }

        _uploads[(userId, hash)] = clock.GetUtcNow();
    }

    public bool UploadedBy(string userId, ContentHash hash) =>
        _uploads.TryGetValue((userId, hash), out var at) && clock.GetUtcNow() - at < Lifetime;

    /// <summary>Abgelaufene weg; reicht das nicht, die älteste Hälfte (wer dann committet, lädt die Blöcke eben noch einmal).</summary>
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, at) in _uploads)
        {
            if (now - at >= Lifetime)
            {
                _uploads.TryRemove(key, out _);
            }
        }

        if (_uploads.Count >= MaxEntries)
        {
            foreach (var (key, _) in _uploads.OrderBy(e => e.Value).Take(_uploads.Count / 2))
            {
                _uploads.TryRemove(key, out _);
            }
        }
    }
}
