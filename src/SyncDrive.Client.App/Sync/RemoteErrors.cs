using SyncDrive.Contracts;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Sync;

/// <summary>Ordnet Protokoll-Fehlercodes ein, damit jeder Adapter (HTTP, im Prozess) gleich reagiert.</summary>
public static class RemoteErrors
{
    /// <summary>Kein Server erreichbar oder Zeitüberschreitung (kein Code des Servers).</summary>
    public const string Network = "network";

    public static SyncErrorKind KindOf(string code) => code switch
    {
        // Neu bewerten genügt: jemand war schneller, ein Block fehlt noch, Inhalt kam anders an als gehasht.
        ErrorCode.RevisionConflict or ErrorCode.BlocksMissing or ErrorCode.BlockHashMismatch
            or ErrorCode.ParentMissing or ErrorCode.CursorExpired or Network => SyncErrorKind.Transient,
        ErrorCode.Unauthorized or ErrorCode.SpaceNotFound => SyncErrorKind.Fatal,
        _ => SyncErrorKind.Permanent,
    };

    public static SyncError From(string code, string message) => new(KindOf(code), code, message);
}
