using SyncDrive.Core.Filtering;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Settings;

/// <summary>Was der Nutzer je Sync-Aufgabe unter „Erweitert → Ordner- und Dateifilter“ einstellt.</summary>
/// <param name="MaxFileSizeBytes">0 = unbegrenzt.</param>
/// <param name="ExcludedFolders">Selektive Synchronisierung: abgewählte Ordner.</param>
public sealed record TaskFilterSettings(
    long MaxFileSizeBytes = 0,
    IReadOnlyList<string>? ExcludedNames = null,
    IReadOnlyList<string>? ExcludedExtensions = null,
    IReadOnlyList<RelativePath>? ExcludedFolders = null);

/// <summary>
/// Standardfilter, die jede neue Aufgabe mitbringt:
/// Systemnamen, die nie synchronisiert werden, Office-/Editor-Hilfsdateien, versteckte Dateien, Längenlimits. Die Listen sind Verhalten,
/// nicht Gestaltung; wir übernehmen sie, damit Nutzer dieselben Dateien im Verlauf als „nicht synchronisiert“ sehen.
/// </summary>
public static class DefaultFilters
{
    /// <summary>Namen (Dateien und Ordner), die Betriebssystem oder NAS selbst verwalten.</summary>
    public static readonly IReadOnlyList<string> SystemNames =
    [
        "@tmp", "@eaDir", "#recycle", "#snapshot", "@sharebin", "$Recycle.Bin",
        "desktop.ini", ".ds_store", "thumbs.db", "Icon\r", "System Volume Information",
        "Program Files", "Program Files (x86)", "ProgramData",

        // macOS: Ordner, die Finder, Spotlight und Time Machine selbst anlegen (Dateien mit Punkt fängt schon die Präfixregel)
        ".Trashes", ".fseventsd", ".Spotlight-V100", ".TemporaryItems", ".DocumentRevisions-V100", ".AppleDouble", ".AppleDB",
    ];

    /// <summary>Dateinamen, die nur als Datei ausgeschlossen sind.</summary>
    public static readonly IReadOnlyList<string> TemporaryFileNames = ["tmp", "temp"];

    /// <summary>Endungen temporärer bzw. nicht sinnvoll synchronisierbarer Dateien.</summary>
    public static readonly IReadOnlyList<string> TemporaryExtensions = ["lnk", "pst", "swp", "temp", "tmp"];

    /// <summary>Höchste Pfadlänge in Zeichen (relativ zum Sync-Ordner).</summary>
    public const int MaxPathLength = 247;

    /// <summary>Standard-Größenlimit je Datei: 64 GiB.</summary>
    public const long DefaultMaxFileSize = 64L * 1024 * 1024 * 1024;

    public static FilterEngine Create(TaskFilterSettings? task = null)
    {
        task ??= new TaskFilterSettings();
        var rules = new List<IFilterRule>
        {
            new NameRule(SystemNames),
            new NameRule(TemporaryFileNames, filesOnly: true),
            new PrefixRule(["~"], filesOnly: true), // Office-Sperrdateien wie ~$Bericht.docx
            new PrefixRule(["."], filesOnly: true), // versteckte Dateien (Unix-Konvention)
            new ExtensionRule(TemporaryExtensions),
            new PathLengthRule(MaxPathLength),
            new SizeRule(task.MaxFileSizeBytes > 0 ? Math.Min(task.MaxFileSizeBytes, DefaultMaxFileSize) : DefaultMaxFileSize),
        };

        if (task.ExcludedNames is { Count: > 0 } names)
        {
            rules.Add(new NameRule(names));
        }

        if (task.ExcludedExtensions is { Count: > 0 } extensions)
        {
            rules.Add(new ExtensionRule(extensions));
        }

        if (task.ExcludedFolders is { Count: > 0 } folders)
        {
            rules.Add(new SelectiveSyncRule(folders));
        }

        return new FilterEngine(rules);
    }
}
