using Microsoft.Win32;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Tasks;

namespace SyncDrive.Client.Daemon;

/// <summary>Was die Aufgabenregeln über diesen Rechner wissen müssen: Dateisystem, Benutzerordner, Ordner anderer Sync-Dienste.</summary>
public static class LocalEnvironment
{
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];
    private static readonly string[] HomeCloudFolders = ["Dropbox", "iCloudDrive", "Google Drive", "My Drive"];

    public static TaskEnvironment For(bool canWriteRemote)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new TaskEnvironment(IsNtfs, home, CloudFolders(home), canWriteRemote);
    }

    private static bool IsNtfs(string path)
    {
        try
        {
            return string.Equals(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>OneDrive meldet sich über Umgebungsvariablen; Dropbox, iCloud und Google Drive liegen üblicherweise im Benutzerordner.</summary>
    private static List<string> CloudFolders(string home)
    {
        var candidates = OneDriveVariables.Select(Environment.GetEnvironmentVariable).Concat(HomeCloudFolders.Select(n => Path.Combine(home, n)));
        return [.. candidates.OfType<string>().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>„Beim Anmelden starten“: Eintrag unter <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> für die Oberfläche (sie startet den Daemon).</summary>
public sealed class WindowsAutostart(string executable) : IAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SyncDrive";

    public void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{executable}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
