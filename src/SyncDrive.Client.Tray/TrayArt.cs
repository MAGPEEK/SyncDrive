using System.Runtime.InteropServices;
using Avalonia.Controls;
using Microsoft.Win32;
using SyncDrive.Client.App.Status;
using SyncDrive.Client.Tray.Controls;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Tray;

/// <summary>
/// Bilder des Tray-Symbols: in der Größe, die Windows für den Infobereich nimmt (ein größeres Bild verkleinert es unscharf), in der Farbe
/// passend zur Taskleiste; die Bilder werden je Zustand einmal gezeichnet und wiederverwendet (das Kreisen braucht zwölf).
/// </summary>
internal static class TrayArt
{
    private const int SmallIconWidth = 49; // SM_CXSMICON

    /// <summary>Menüleiste: 18 Punkt hoch, auf Retina-Bildschirmen (alle M-Macs) doppelt.</summary>
    private const int MenuBarSize = 36;
    private static readonly Dictionary<(TrayState, bool, bool, int, bool, int), WindowIcon> Cache = [];
    private static readonly Dictionary<(TrayState, bool, int), byte[]> MacCache = [];

    public static WindowIcon Icon(ShellViewModel shell, int spin)
    {
        var state = shell.TrayState;
        var key = (state, shell.HasNotifications, shell.Settings.MinimalTrayIcon, Size(), DarkTaskbar(), state == TrayState.Syncing ? spin : 0);
        if (!Cache.TryGetValue(key, out var icon))
        {
            using var stream = new MemoryStream();
            Badges.TrayBitmap(key.state, key.Item2, key.Item3, key.Item4, key.Item5, key.Item6).Save(stream);
            stream.Position = 0;
            icon = new WindowIcon(stream);
            Cache[key] = icon;
        }

        return icon;
    }

    /// <summary>macOS: Vorlagenbild der Menüleiste als PNG für <see cref="MacStatusItem"/>; je Zustand einmal gezeichnet.</summary>
    public static byte[] MacImage(ShellViewModel shell, int spin)
    {
        var state = shell.TrayState;
        var key = (state, shell.HasNotifications, state == TrayState.Syncing ? spin : 0);
        if (!MacCache.TryGetValue(key, out var png))
        {
            using var stream = new MemoryStream();
            Badges.TemplateTrayBitmap(state, shell.HasNotifications, MenuBarSize, key.Item3).Save(stream);
            png = stream.ToArray();
            MacCache[key] = png;
        }

        return png;
    }

    /// <summary>Kantenlänge eines kleinen Symbols bei der aktuellen Skalierung (16 bei 100 %, 24 bei 150 %, 32 bei 200 %).</summary>
    private static int Size() => OperatingSystem.IsWindows() && GetSystemMetrics(SmallIconWidth) is >= 16 and <= 256 and var size ? size : 32;

    /// <summary>Dunkle Taskleiste (Standard unter Windows 11) → weißes Zeichen; helle → dunkles.</summary>
    private static bool DarkTaskbar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is not 1;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetSystemMetrics(int index);
}
