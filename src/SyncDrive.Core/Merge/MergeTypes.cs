namespace SyncDrive.Core.Merge;

public enum SyncDirection
{
    TwoWay,
    UploadOnly,
    DownloadOnly,
}

public enum ConflictPolicy
{
    /// <summary>Standard: Nichts geht verloren. Remote behält den Pfad, die lokale Version wandert in eine Konfliktkopie.</summary>
    KeepBoth,
    NewerWins,
    ClientWins,
    ServerWins,
}

public enum ConflictKind
{
    ModifyModify,
    ModifyDelete,
    DeleteModify,
    AddAdd,
    TypeChange,
}

public enum Side
{
    Local,
    Remote,
}

/// <summary>Was mit der unterlegenen Version geschieht.</summary>
public enum LoserHandling
{
    /// <summary>Es gibt keine unterlegene Version (die Gegenseite hat gelöscht).</summary>
    None,

    /// <summary>Als Konfliktkopie neben dem Original behalten.</summary>
    CopyAside,

    /// <summary>In den Papierkorb bzw. die Versionshistorie.</summary>
    Trash,
}

public sealed record MergeOptions(
    SyncDirection Direction = SyncDirection.TwoWay,
    ConflictPolicy Policy = ConflictPolicy.KeepBoth,
    bool RenameConflict = true,
    bool IgnoreLocalDelete = false,
    bool IgnoreRemoteDelete = false);

/// <summary>Ergebnis der Bewertung eines Pfads. Reine Beschreibung; ausgeführt wird sie von Handlern.</summary>
public abstract record SyncAction
{
    private SyncAction()
    {
    }

    /// <summary>Beide Seiten stimmen mit der Basis überein (oder Änderungen sind per Richtung ausgeblendet).</summary>
    public sealed record NoOp : SyncAction;

    /// <summary>Beide Seiten haben dasselbe Ergebnis erreicht; nur die Basis wird nachgezogen.</summary>
    public sealed record AdoptBase : SyncAction;

    public sealed record Upload : SyncAction;

    public sealed record Download : SyncAction;

    public sealed record RemoteDelete : SyncAction;

    public sealed record LocalDelete : SyncAction;

    public sealed record Conflict(ConflictKind Kind, Side Winner, LoserHandling Loser) : SyncAction;
}
