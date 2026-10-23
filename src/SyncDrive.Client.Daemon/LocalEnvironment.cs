using System.Xml.Linq;
using Microsoft.Win32;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Tasks;

namespace SyncDrive.Client.Daemon;

/// <summary>Was die Aufgabenregeln über diesen Rechner wissen müssen: Dateisystem, Benutzerordner, Ordner anderer Sync-Dienste.</summary>
public static class LocalEnvironment
{
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];
    // macOS: iCloud Drive und alle Anbieter mit File Provider (OneDrive, Dropbox, Google Drive …) liegen unter Library
    private static readonly string[] HomeCloudFolders =
        ["Dropbox", "iCloudDrive", "Google Drive", "My Drive", "Library/Mobile Documents", "Library/CloudStorage"];

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

/// <summary>
/// „Beim Anmelden öffnen“ unter macOS: ein LaunchAgent des Benutzers (<c>~/Library/LaunchAgents</c>) startet die Oberfläche im Hintergrund,
/// die den Daemon startet. Braucht weder Signatur noch Adminrecht; mit der signierten App folgt <c>SMAppService</c> (ADR-14).
/// </summary>
/// <param name="executable">Die Oberfläche im App-Paket, <c>…/SyncDrive.app/Contents/MacOS/syncdrive</c>.</param>
/// <param name="launchAgents">Ordner der LaunchAgents; in Tests ein Temp-Ordner.</param>
public sealed class MacLaunchAgent(string executable, string? launchAgents = null) : IAutostart
{
    public const string Label = "org.syncdrive.client";

    public string PlistPath { get; } = Path.Combine(
        launchAgents ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"), Label + ".plist");

    /// <summary>Aus dem geöffneten Disk-Image gestartet (<c>/Volumes/…</c>): der Pfad verschwindet beim Auswerfen.</summary>
    public static bool RunsFromDiskImage(string executable) => executable.StartsWith("/Volumes/", StringComparison.Ordinal);

    public void Apply(bool enabled)
    {
        // Ein Autostart auf das Disk-Image liefe nach dem Auswerfen ins Leere; erst aus „Programme“ heraus anlegen
        if (!enabled || RunsFromDiskImage(executable))
        {
            File.Delete(PlistPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        var plist = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement(
                "plist",
                new XAttribute("version", "1.0"),
                new XElement(
                    "dict",
                    new XElement("key", "Label"),
                    new XElement("string", Label),
                    new XElement("key", "ProgramArguments"),
                    new XElement(
                        "array", new XElement("string", executable), new XElement("string", "--background")),
                    new XElement("key", "RunAtLoad"),
                    new XElement("true"),
                    new XElement("key", "ProcessType"),
                    new XElement("string", "Interactive"))));
        var temp = PlistPath + ".tmp";
        plist.Save(temp);
        File.Move(temp, PlistPath, overwrite: true);
    }
}
