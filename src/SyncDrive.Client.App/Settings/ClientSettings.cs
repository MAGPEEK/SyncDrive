using SyncDrive.Core.Merge;

namespace SyncDrive.Client.App.Settings;

/// <summary>Verhalten beim Neustart bzw. Fortsetzen der Verbindung für Dateien, die lokal gelöscht wurden, während der Client nicht lief.</summary>
public enum OnRestartLocalDeletes
{
    /// <summary>Vom Server wieder holen (Standard).</summary>
    Refetch,

    /// <summary>Die Löschung auch auf dem Server ausführen.</summary>
    PropagateDelete,
}

/// <summary>Die zwei Konfliktstrategien, die der Client dem Nutzer anbietet (ADR-5a).</summary>
public enum ConflictStrategy
{
    /// <summary>Neueste Änderung behalten (Standard).</summary>
    NewerWins,

    /// <summary>Version auf dem Server behalten.</summary>
    ServerWins,
}

public enum ProxyMode
{
    None,
    Auto,
    Manual,
}

/// <summary>Das Passwort steht nicht hier, sondern im Anmeldedatenspeicher des Betriebssystems.</summary>
public sealed record ProxySettings(
    ProxyMode Mode = ProxyMode.None,
    string? Host = null,
    int? Port = null,
    bool UseAuthentication = false,
    string? UserName = null);

/// <summary>
/// Globale Einstellungen des Clients, Eintrag für Eintrag wie die fünf Reiter der Einstellungen.
/// </summary>
public sealed record ClientSettings
{
    // General
    public bool StartWithWindows { get; init; } = true;

    public OnRestartLocalDeletes OnRestart { get; init; } = OnRestartLocalDeletes.Refetch;

    public ConflictStrategy Conflict { get; init; } = ConflictStrategy.NewerWins;

    /// <summary>Verworfene Version als Konfliktkopie behalten (sonst in den Papierkorb).</summary>
    public bool RenameDiscarded { get; init; } = true;

    /// <summary>Name der Sync-Aufgabe, deren Ordner die Desktopverknüpfung öffnet; null = erste Aufgabe.</summary>
    public string? DesktopShortcutFolder { get; init; }

    /// <summary>Sprachcode oder <c>system</c>.</summary>
    public string Language { get; init; } = "system";

    // Notifications
    public bool ShowDesktopNotifications { get; init; } = true;

    // Display
    public bool MinimalTrayIcon { get; init; } = true;

    public bool ShowContextMenu { get; init; } = true;

    public bool ShowOverlays { get; init; } = true;

    public bool ShowOverlaysForUnsynced { get; init; } = true;

    // Proxy
    public ProxySettings Proxy { get; init; } = new();

    /// <summary>
    /// Übersetzt die Nutzerwahl in die Optionen der <see cref="MergeEngine"/>. Die Option „lokal Gelöschtes neu holen“ gilt nur für
    /// Löschungen, die beim Start entdeckt werden (Rescan); Löschungen, die der laufende Client beobachtet, werden weitergegeben.
    /// </summary>
    public MergeOptions ToMergeOptions(SyncDirection direction, bool startupRescan, bool ignoreRemoteDelete = false) => new(
        direction,
        Conflict == ConflictStrategy.NewerWins ? ConflictPolicy.NewerWins : ConflictPolicy.ServerWins,
        RenameDiscarded,
        IgnoreLocalDelete: startupRescan && OnRestart == OnRestartLocalDeletes.Refetch,
        IgnoreRemoteDelete: ignoreRemoteDelete);
}
