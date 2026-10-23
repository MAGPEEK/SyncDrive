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

/// <summary>
/// Warum eine Anmeldung über den Identitätsanbieter scheiterte: der Server leitet mit <c>/?signin_error=&lt;Code&gt;</c> zurück, die
/// Anmeldeseite zeigt den passenden Text. Nur feste Codes, damit niemand über einen Link eigenen Text auf die Seite bringt.
/// </summary>
public static class OAuthRefusal
{
    public const string Off = "off";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string Provider = "provider";
    public const string NoName = "no-name";
    public const string NoAccount = "no-account";
    public const string LinkedElsewhere = "linked-elsewhere";
    public const string Gone = "gone";
    public const string Locked = "locked";
}
