using SyncDrive.Client.App.Settings;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.App.Tasks;

/// <summary>Eine Serververbindung (Hauptfenster: Servergruppe mit Name und Adresse). Der Geräte-Token liegt im <see cref="ICredentialStore"/>.</summary>
public sealed record ConnectionInfo(
    string Id,
    string DisplayName,
    Uri ServerUrl,
    string UserName,
    string DeviceId,
    string? PinnedCertificateSha256);

/// <summary>Eine Sync-Aufgabe, wie sie der Assistent anlegt und „Sync Rules“ ändert.</summary>
/// <param name="LocalPath">Absoluter lokaler Ordner.</param>
/// <param name="KeepLocallyDeletedOnServer">Nur bei <see cref="SyncDirection.UploadOnly"/>: lokal Gelöschtes bleibt auf dem Server (Standard).</param>
public sealed record SyncTaskDefinition(
    string Id,
    string ConnectionId,
    string SpaceId,
    RelativePath RemotePath,
    string LocalPath,
    SyncDirection Direction = SyncDirection.TwoWay,
    bool KeepLocallyDeletedOnServer = true,
    bool OnDemand = false,
    bool AdvancedConsistencyCheck = false,
    TaskFilterSettings? Filters = null,
    bool Paused = false);

public enum TaskRuleViolation
{
    /// <summary>Mehr als <see cref="SyncTaskRules.MaxTasks"/> Aufgaben.</summary>
    TooManyTasks,

    /// <summary>Der lokale Ordner ist derselbe wie, liegt in oder enthält den Ordner einer anderen Aufgabe.</summary>
    LocalFolderOverlapsTask,

    /// <summary>Der lokale Ordner liegt in einem Ordner, den ein anderer Cloud-Dienst synchronisiert (oder enthält ihn).</summary>
    LocalFolderInOtherCloud,

    /// <summary>Nur Leserecht auf dem Server: nur „nur herunterladen“ ist möglich.</summary>
    ReadOnlyRequiresDownloadOnly,

    /// <summary>On-Demand braucht ein NTFS-Laufwerk.</summary>
    OnDemandRequiresNtfs,

    /// <summary>On-Demand nicht im Stamm eines Laufwerks oder des Benutzerordners.</summary>
    OnDemandNotInDriveOrHomeRoot,
}

/// <summary>Was die Regeln über die Umgebung wissen müssen; in Tests frei wählbar.</summary>
/// <param name="CloudFolders">Ordner anderer Sync-Dienste (OneDrive, iCloud, Dropbox …).</param>
public sealed record TaskEnvironment(
    Func<string, bool> IsNtfs,
    string UserHome,
    IReadOnlyList<string> CloudFolders,
    bool CanWriteRemote);

/// <summary>Regeln für Sync-Aufgaben.</summary>
public static class SyncTaskRules
{
    public const int MaxTasks = 64;

    public static IReadOnlyList<TaskRuleViolation> Validate(
        SyncTaskDefinition candidate, IReadOnlyCollection<SyncTaskDefinition> existing, TaskEnvironment environment)
    {
        var violations = new List<TaskRuleViolation>();
        var others = existing.Where(t => t.Id != candidate.Id).ToList();
        if (others.Count + 1 > MaxTasks)
        {
            violations.Add(TaskRuleViolation.TooManyTasks);
        }

        if (others.Any(t => Overlaps(t.LocalPath, candidate.LocalPath)))
        {
            violations.Add(TaskRuleViolation.LocalFolderOverlapsTask);
        }

        if (environment.CloudFolders.Any(c => Overlaps(c, candidate.LocalPath)))
        {
            violations.Add(TaskRuleViolation.LocalFolderInOtherCloud);
        }

        if (!environment.CanWriteRemote && candidate.Direction != SyncDirection.DownloadOnly)
        {
            violations.Add(TaskRuleViolation.ReadOnlyRequiresDownloadOnly);
        }

        if (candidate.OnDemand)
        {
            if (!environment.IsNtfs(candidate.LocalPath))
            {
                violations.Add(TaskRuleViolation.OnDemandRequiresNtfs);
            }

            var full = Normalize(candidate.LocalPath);
            if (full == Normalize(Path.GetPathRoot(full) ?? full) || full == Normalize(environment.UserHome))
            {
                violations.Add(TaskRuleViolation.OnDemandNotInDriveOrHomeRoot);
            }
        }

        return violations;
    }

    /// <summary>Gleich, darin oder darüber (ohne Beachtung der Schreibweise, Windows-Semantik).</summary>
    public static bool Overlaps(string a, string b)
    {
        var x = Normalize(a) + Path.DirectorySeparatorChar;
        var y = Normalize(b) + Path.DirectorySeparatorChar;
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
