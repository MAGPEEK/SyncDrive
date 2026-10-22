namespace SyncDrive.Contracts;

/// <summary>Werte für <see cref="CommitRequest.Op"/>.</summary>
public static class CommitOps
{
    public const string Put = "put";
    public const string Mkdir = "mkdir";
    public const string Delete = "delete";
    public const string Move = "move";

    /// <summary>Kopie von <c>path</c> nach <c>newPath</c> (ganzer Teilbaum, ohne Datenübertragung).</summary>
    public const string Copy = "copy";
}

/// <summary>Werte für <see cref="ChangeEntry.Op"/>.</summary>
public static class ChangeOps
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";
    public const string Move = "move";

    /// <summary>Sperre gesetzt oder aufgehoben (A15). Inhalt unverändert; Clients aktualisieren nur den Sperrstatus.</summary>
    public const string Lock = "lock";
}

/// <summary>Werte für <c>kind</c> in Knoten und Feed-Einträgen.</summary>
public static class NodeKinds
{
    public const string File = "file";
    public const string Directory = "dir";
}

/// <summary>Arten öffentlicher Links (M6b).</summary>
public static class LinkKinds
{
    /// <summary>Jeder mit dem Link kann ansehen und herunterladen.</summary>
    public const string Share = "share";

    /// <summary>Dateianfrage: Jeder mit dem Link kann Dateien in den Ordner hochladen, sieht aber dessen Inhalt nicht.</summary>
    public const string Request = "request";
}
