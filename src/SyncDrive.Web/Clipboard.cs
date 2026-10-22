using Microsoft.JSInterop;

namespace SyncDrive.Web;

/// <summary>Zwischenablage des Browsers ohne eigenes Skript (die Seite erlaubt nur eigene Skripte, ADR-11).</summary>
internal static class Clipboard
{
    /// <summary>False, wenn der Browser es verweigert (z. B. ohne HTTPS); dann zeigt die Oberfläche den Link zum Abschreiben.</summary>
    public static async Task<bool> CopyAsync(IJSRuntime js, string text)
    {
        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return true;
        }
        catch (JSException)
        {
            return false;
        }
    }
}
