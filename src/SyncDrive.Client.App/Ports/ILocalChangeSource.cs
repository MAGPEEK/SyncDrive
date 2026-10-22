using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Ports;

/// <summary>Ein Ereignis aus dem Sync-Ordner. Es bedeutet nur „bitte neu bewerten“ (ADR-2), nicht was genau passiert ist.</summary>
public abstract record LocalChange
{
    private LocalChange()
    {
    }

    public sealed record PathChanged(RelativePath Path) : LocalChange;

    /// <summary>Umbenannt oder verschoben innerhalb des Sync-Ordners; wird nach Möglichkeit als Server-Verschiebung übernommen.</summary>
    public sealed record Moved(RelativePath From, RelativePath To) : LocalChange;

    /// <summary>Ereignisse gingen verloren (Puffer übergelaufen): Der Teilbaum muss vollständig neu verglichen werden.</summary>
    public sealed record Overflow : LocalChange;
}

/// <summary>Meldet Änderungen im Sync-Ordner (Windows: <c>FileSystemWatcher</c>; später Cloud-Files-Rückrufe).</summary>
public interface ILocalChangeSource : IDisposable
{
    IAsyncEnumerable<LocalChange> ReadAllAsync(CancellationToken ct);
}
