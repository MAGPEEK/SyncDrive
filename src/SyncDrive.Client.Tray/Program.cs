using System.Security.Cryptography;
using System.Text;
using Avalonia;

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
        using var single = new Mutex(initiallyOwned: true, $@"Local\SyncDrive.Tray.{userKey}", out var first);
        if (!first)
        {
            return 0; // Oberfläche läuft schon; ihr Tray-Symbol ist da
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
