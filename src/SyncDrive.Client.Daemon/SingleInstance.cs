namespace SyncDrive.Client.Daemon;

/// <summary>
/// Höchstens ein Prozess je Benutzer und Rolle (Daemon, Oberfläche). Windows: benannter Mutex, das System gibt ihn mit dem Prozess frei.
/// macOS: Dateisperre (<c>flock</c>) auf <paramref name="lockFolder"/>; auch sie fällt mit dem Prozess, selbst nach einem Absturz oder
/// „Sofort beenden“. Ein benannter Mutex hinterlässt dort eine Datei, und jeder weitere Start hielte sich für den zweiten (ADR-14).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly IDisposable _hold;

    private SingleInstance(IDisposable hold) => _hold = hold;

    /// <summary>Null, wenn schon ein Prozess dieser Rolle läuft.</summary>
    public static SingleInstance? TryAcquire(string name, string lockFolder)
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out var first);
            if (first)
            {
                return new SingleInstance(mutex);
            }

            mutex.Dispose();
            return null;
        }

        Directory.CreateDirectory(lockFolder);
        try
        {
            return new SingleInstance(new FileStream(
                Path.Combine(lockFolder, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); // Datei bleibt liegen: Löschen beim Schließen öffnete ein Rennen um zwei Sperren
        }
        catch (IOException)
        {
            return null; // ein anderer Prozess hält die Sperre
        }
    }

    public void Dispose() => _hold.Dispose();
}
