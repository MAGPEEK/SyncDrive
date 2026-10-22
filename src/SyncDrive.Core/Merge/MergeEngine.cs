using SyncDrive.Core.Model;

namespace SyncDrive.Core.Merge;

/// <summary>
/// Drei-Wege-Vergleich eines Pfads: Basis (zuletzt abgeglichen), lokal, remote.
/// Reine Funktion ohne I/O.
/// </summary>
public static class MergeEngine
{
    public static SyncAction Decide(PathTriple triple, MergeOptions options)
    {
        ArgumentNullException.ThrowIfNull(triple);
        ArgumentNullException.ThrowIfNull(options);

        var (_, baseState, local, remote) = triple;

        var localChanged = !NodeState.Same(local, baseState);
        var remoteChanged = !NodeState.Same(remote, baseState);

        if (!localChanged && !remoteChanged)
        {
            return new SyncAction.NoOp();
        }

        if (NodeState.Same(local, remote))
        {
            return new SyncAction.AdoptBase();
        }

        localChanged = IsVisible(localChanged, local, baseState, options, Side.Local);
        remoteChanged = IsVisible(remoteChanged, remote, baseState, options, Side.Remote);

        return (localChanged, remoteChanged) switch
        {
            (false, false) => new SyncAction.NoOp(),
            (true, false) => local is null ? new SyncAction.RemoteDelete() : new SyncAction.Upload(),
            (false, true) => remote is null ? new SyncAction.LocalDelete() : new SyncAction.Download(),
            _ => ResolveConflict(triple, options),
        };
    }

    /// <summary>Blendet Änderungen aus, die Richtung oder Lösch-Optionen ausdrücklich ignorieren.</summary>
    private static bool IsVisible(bool changed, NodeState? state, NodeState? baseState, MergeOptions options, Side side)
    {
        if (!changed)
        {
            return false;
        }

        var isDelete = state is null && baseState is not null;
        return side switch
        {
            Side.Local => options.Direction != SyncDirection.DownloadOnly && !(isDelete && options.IgnoreLocalDelete),
            _ => options.Direction != SyncDirection.UploadOnly && !(isDelete && options.IgnoreRemoteDelete),
        };
    }

    private static SyncAction.Conflict ResolveConflict(PathTriple triple, MergeOptions options)
    {
        var (_, baseState, local, remote) = triple;
        var kind = ClassifyConflict(baseState, local, remote);

        // Löschung gegen Änderung: Der Inhalt gewinnt, außer die Richtlinie bestimmt ausdrücklich eine Seite.
        if (kind is ConflictKind.ModifyDelete or ConflictKind.DeleteModify)
        {
            var contentSide = local is null ? Side.Remote : Side.Local;
            var winner = options.Policy switch
            {
                ConflictPolicy.ClientWins => Side.Local,
                ConflictPolicy.ServerWins => Side.Remote,
                _ => contentSide,
            };

            // Setzt sich die Löschung durch, wandert der Inhalt in den Papierkorb; sonst gibt es keinen Verlierer.
            return new SyncAction.Conflict(kind, winner, winner == contentSide ? LoserHandling.None : LoserHandling.Trash);
        }

        var winnerSide = options.Policy switch
        {
            ConflictPolicy.ClientWins => Side.Local,
            ConflictPolicy.ServerWins => Side.Remote,
            ConflictPolicy.NewerWins => local!.MTime > remote!.MTime ? Side.Local : Side.Remote,
            _ => Side.Remote,
        };

        var loser = options.Policy == ConflictPolicy.KeepBoth || options.RenameConflict
            ? LoserHandling.CopyAside
            : LoserHandling.Trash;

        return new SyncAction.Conflict(kind, winnerSide, loser);
    }

    private static ConflictKind ClassifyConflict(NodeState? baseState, NodeState? local, NodeState? remote)
    {
        if (local is null)
        {
            return ConflictKind.DeleteModify;
        }

        if (remote is null)
        {
            return ConflictKind.ModifyDelete;
        }

        if (local.Kind != remote.Kind)
        {
            return ConflictKind.TypeChange;
        }

        return baseState is null ? ConflictKind.AddAdd : ConflictKind.ModifyModify;
    }
}
