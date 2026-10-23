namespace SyncDrive.Server.Host;

/// <summary>
/// Konfiguration, Abschnitt <c>Server</c>. Im Container über Umgebungsvariablen, z. B.
/// <c>SYNCDRIVE_Server__DataDirectory=/data</c>.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>Wurzel für <c>server.db</c> und <c>blocks/</c>. Im Container ein Volume (lokal, kein NFS: SQLite/WAL).</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Lehnt Anfragen ohne TLS ab. Hinter einem TLS-Proxy zusammen mit <see cref="TrustForwardedHeaders"/> nutzen.</summary>
    public bool RequireTls { get; set; } = true;

    /// <summary>Wertet <c>X-Forwarded-Proto/For</c> beliebiger Absender aus; nur setzen, wenn der Port nur der Proxy erreicht.</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>Legt beim ersten Start (ohne Benutzer) dieses Admin-Konto an.</summary>
    public string? AdminUserName { get; set; }

    public string? AdminPassword { get; set; }

    /// <summary>Alternative zu <see cref="AdminPassword"/>: Datei mit dem Passwort (Docker/Kubernetes Secret).</summary>
    public string? AdminPasswordFile { get; set; }

    public int Argon2MemoryKiB { get; set; } = 65536;

    public int Argon2Iterations { get; set; } = 3;

    public int Argon2Parallelism { get; set; } = 4;

    public int MaxBlockBytes { get; set; } = 8 * 1024 * 1024;

    public int MaxLongPollSeconds { get; set; } = 60;

    /// <summary>Größte Datei, die jemand über eine Dateianfrage (öffentlicher Link) hochladen darf; Standard 4 GiB.</summary>
    public long MaxPublicUploadBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Wohin „Desktop-App herunterladen“ in der Weboberfläche führt (Profilmenü → Desktop- und Mobil-Apps). Standard: die Release-Seite
    /// des Projekts; wer die Pakete selbst bereitstellt, trägt hier seine Adresse ein.
    /// </summary>
    public string DesktopClientUrl { get; set; } = "https://github.com/MAGPEEK/SyncDrive/releases/latest";
}
