using SyncDrive.Contracts;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Ports;

/// <summary>Zuletzt erfolgreich abgeglichener Stand eines Pfads: die <c>Basis</c> des Dreiwege-Vergleichs.</summary>
public sealed record SyncedNode(RelativePath Path, string NodeId, NodeState State, long RemoteRevision, DateTimeOffset SyncedAt);

/// <summary>Zustand einer Sync-Sitzung (Basis und Feed-Position). Wird in derselben Reihenfolge geschrieben, in der Aktionen gelingen.</summary>
public interface ISessionState
{
    Task<SyncedNode?> GetAsync(RelativePath path, CancellationToken ct);

    Task SaveAsync(SyncedNode node, CancellationToken ct);

    /// <summary>Entfernt den Pfad und alles darunter (Verzeichnis gelöscht oder ausgeschlossen).</summary>
    Task RemoveSubtreeAsync(RelativePath path, CancellationToken ct);

    /// <summary>Alle Basis-Einträge unterhalb von <paramref name="root"/> (ohne ihn selbst), für den Rescan.</summary>
    IAsyncEnumerable<SyncedNode> ListAsync(RelativePath root, CancellationToken ct);

    Task<long> GetFeedCursorAsync(CancellationToken ct);

    Task SetFeedCursorAsync(long revision, CancellationToken ct);
}

/// <summary>Ein Knoten auf dem Server. <see cref="State"/> ist null bei einem Grabstein (gelöscht).</summary>
public sealed record RemoteNode(string NodeId, NodeState? State, long Revision, IReadOnlyList<ContentHash> Blocks);

/// <summary>Der Server aus Sicht einer Sync-Sitzung. Fehler kommen als <see cref="Result{T}"/>, auch Netzfehler (<c>Transient</c>).</summary>
public interface IRemoteApi
{
    /// <summary>Null im Erfolgsfall: der Pfad hat auf dem Server nie existiert.</summary>
    Task<Result<RemoteNode?>> GetNodeAsync(RelativePath path, CancellationToken ct);

    Task<Result<IReadOnlyList<SnapshotEntry>>> GetSnapshotAsync(RelativePath root, CancellationToken ct);

    /// <summary>Neueste Revision des Space; der Feed-Cursor nach einem vollständigen Abgleich.</summary>
    Task<Result<long>> GetLatestRevisionAsync(CancellationToken ct);

    /// <summary>Änderungen nach <paramref name="after"/>; mit <paramref name="wait"/> als Long-Poll.</summary>
    Task<Result<ChangesResponse>> GetChangesAsync(long after, int limit, TimeSpan? wait, CancellationToken ct);

    Task<Result<IReadOnlyList<ContentHash>>> MissingBlocksAsync(IReadOnlyList<ContentHash> hashes, CancellationToken ct);

    Task<Result<bool>> UploadBlockAsync(ContentHash hash, Stream content, CancellationToken ct);

    /// <summary>Strom des Blocks; der Aufrufer gibt ihn frei.</summary>
    Task<Result<Stream>> DownloadBlockAsync(ContentHash hash, CancellationToken ct);

    Task<Result<CommitResponse>> CommitAsync(CommitRequest request, string? idempotencyKey, CancellationToken ct);
}

public enum ActivityAction
{
    Upload,
    Download,
    Delete,
    Conflict,
    Move,
}

/// <summary><c>Unsynced</c> heißt: bewusst nicht synchronisiert (Filter, Rechte); der Grund steht in <see cref="ActivityEntry.Reason"/>.</summary>
public enum ActivityResult
{
    Done,
    Unsynced,
    Failed,
}

/// <summary>Eintrag des Verlaufs (Tray „Recently Changed“, Seite „Logs“).</summary>
public sealed record ActivityEntry(
    DateTimeOffset At,
    ActivityAction Action,
    RelativePath Path,
    bool IsDirectory,
    ActivityResult Result,
    string? Reason = null);

public interface IActivityLog
{
    Task AppendAsync(ActivityEntry entry, CancellationToken ct);

    /// <summary>Neueste Einträge zuerst (Tray „Recently Changed“, Seite „Logs“).</summary>
    Task<IReadOnlyList<ActivityEntry>> RecentAsync(int limit, CancellationToken ct);
}

public sealed class NullActivityLog : IActivityLog
{
    public Task AppendAsync(ActivityEntry entry, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<ActivityEntry>> RecentAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActivityEntry>>([]);
}
