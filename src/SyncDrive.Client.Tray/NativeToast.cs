using System.ComponentModel;
using System.Diagnostics;
using SyncDrive.Client.Tray.Controls;

namespace SyncDrive.Client.Tray;

/// <summary>
/// Windows-Benachrichtigung (Info-Center) über <c>syncdrive-cloudreg notify</c>: nur das Hilfsprogramm hat die WinRT-Projektion. Fehlt es
/// (andere Systeme, Entwicklung ohne Windows), zeigt die Oberfläche ihr eigenes Hinweisfenster.
/// </summary>
internal static class NativeToast
{
    private static string? _icon;

    public static bool TryShow(string title, string message)
    {
        if (OperatingSystem.IsMacOS())
        {
            return TryShowOnMac(title, message);
        }

        if (!OperatingSystem.IsWindows() || Helper() is not { } helper)
        {
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "notify", title, message, Icon() })
            {
                start.ArgumentList.Add(argument);
            }

            using var _ = Process.Start(start);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// macOS: Mitteilung über AppleScript (<c>display notification</c>). Das geht ohne signierte App; mit der Signatur folgt
    /// <c>UNUserNotificationCenter</c> unter eigenem Namen und Symbol (ADR-14, Stufe 3). Texte werden als AppleScript-Zeichenketten maskiert.
    /// </summary>
    internal static string AppleScript(string title, string message) =>
        $"display notification {Quote(message)} with title {Quote(title)}";

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool TryShowOnMac(string title, string message)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(AppleScript(title, message));
            using var _ = Process.Start(start);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            return false;
        }
    }

    /// <summary>Im Build liegt das Hilfsprogramm in <c>cloudreg\</c>, in der Installation neben der Oberfläche.</summary>
    private static string? Helper()
    {
        var nested = Path.Combine(AppContext.BaseDirectory, "cloudreg", "syncdrive-cloudreg.exe");
        var flat = Path.Combine(AppContext.BaseDirectory, "syncdrive-cloudreg.exe");
        return File.Exists(nested) ? nested : File.Exists(flat) ? flat : null;
    }

    /// <summary>Das Info-Center liest das Symbol aus einer Datei: einmal je Start als PNG in die Ablage des Benutzers.</summary>
    private static string Icon()
    {
        if (_icon is null)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncDrive");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "notification-icon.png");
            Badges.GlyphBitmap(128).Save(path);
            _icon = path;
        }

        return _icon;
    }
}
