using System.Security.Cryptography;
using System.Text;
using Avalonia;
using SyncDrive.Client.Daemon;

namespace SyncDrive.Client.Tray;

public static class Program
{
    public static IReadOnlyList<string> Arguments { get; private set; } = [];

    /// <summary><c>--background</c>: beim Anmelden gestartet, nur Tray (kein Fenster).</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        if (args is ["--shell", _, _, ..])
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); // Explorer-Menü: kurzer Prozess neben der laufenden Oberfläche
        }

        var userKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName.ToUpperInvariant())))[..16];
        using var single = SingleInstance.TryAcquire($"SyncDrive.Tray.{userKey}", ClientPaths.Default().Root);
        if (single is null)
        {
            return 0; // Oberfläche läuft schon; ihr Tray-Symbol ist da
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // macOS: nur in der Menüleiste, kein Dock-Symbol (LSUIElement allein genügt Avalonia nicht)
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().With(new MacOSPlatformOptions { ShowInDock = false }).LogToTrace();
}
