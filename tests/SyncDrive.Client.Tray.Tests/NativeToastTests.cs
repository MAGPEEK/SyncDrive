namespace SyncDrive.Client.Tray.Tests;

public sealed class NativeToastTests
{
    /// <summary>Datei- und Ordnernamen landen in einer AppleScript-Zeichenkette: Anführungszeichen und Backslashes dürfen sie nicht beenden.</summary>
    [Fact]
    public void Mac_notifications_quote_names_safely()
    {
        var script = NativeToast.AppleScript("SyncDrive", """Bericht "final" \ 2026.pdf: Downloaded""");

        Assert.Equal("""display notification "Bericht \"final\" \\ 2026.pdf: Downloaded" with title "SyncDrive" """.TrimEnd(), script);
    }
}
