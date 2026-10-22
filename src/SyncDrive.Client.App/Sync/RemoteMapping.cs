using SyncDrive.Client.App.Ports;
using SyncDrive.Contracts;
using SyncDrive.Core.Model;

namespace SyncDrive.Client.App.Sync;

/// <summary>Übersetzt Protokoll-DTOs in Client-Werte; gemeinsam für alle <see cref="IRemoteApi"/>-Adapter.</summary>
public static class RemoteMapping
{
    public static RemoteNode ToRemoteNode(NodeDetail detail)
    {
        var n = detail.Node;
        NodeState? state = n.Deleted
            ? null
            : n.Kind == NodeKinds.Directory
                ? NodeState.Directory(n.MTime)
                : NodeState.File(n.Size, n.MTime, n.Hash is null ? null : ContentHash.Parse(n.Hash));
        return new RemoteNode(n.NodeId, state, n.Revision, [.. detail.Blocks.Select(ContentHash.Parse)]);
    }
}
