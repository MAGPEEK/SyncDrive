using SyncDrive.Core.Paths;

namespace SyncDrive.Core.Model;

public enum NodeKind
{
    File,
    Directory,
}

/// <summary>Zustand eines Pfads an einem Ort (Basis, lokal oder remote).</summary>
/// <param name="Hash">Null, solange der Inhalt noch nicht gehasht wurde (schwache Prüfung).</param>
public sealed record NodeState(NodeKind Kind, long Size, DateTimeOffset MTime, ContentHash? Hash = null)
{
    public static NodeState Directory(DateTimeOffset mtime) => new(NodeKind.Directory, 0, mtime);

    public static NodeState File(long size, DateTimeOffset mtime, ContentHash? hash = null) =>
        new(NodeKind.File, size, mtime, hash);

    /// <summary>
    /// Inhaltsgleichheit. Verzeichnisse sind gleich, sobald beide existieren. Bei Dateien zählt der Hash;
    /// fehlt er auf einer Seite, gilt die schwache Prüfung (Größe und mtime).
    /// </summary>
    public static bool Same(NodeState? a, NodeState? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a.Kind != b.Kind)
        {
            return false;
        }

        if (a.Kind == NodeKind.Directory)
        {
            return true;
        }

        if (a.Size != b.Size)
        {
            return false;
        }

        return a.Hash is { } ha && b.Hash is { } hb ? ha == hb : a.MTime == b.MTime;
    }
}

/// <summary>Drei Sichten auf denselben Pfad. Null bedeutet: existiert dort nicht.</summary>
public sealed record PathTriple(RelativePath Path, NodeState? Base, NodeState? Local, NodeState? Remote);
