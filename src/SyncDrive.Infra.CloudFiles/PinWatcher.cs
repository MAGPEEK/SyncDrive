using System.Text;
using static SyncDrive.Infra.CloudFiles.NativeMethods;

namespace SyncDrive.Infra.CloudFiles;

/// <summary>
/// Setzt um, was der Nutzer wählt: „Immer auf diesem Gerät behalten“ (angeheftet) holt den Inhalt, „Speicherplatz freigeben“
/// (losgelöst) gibt ihn frei. Windows ändert dabei nur die Attribute; handeln muss der Anbieter. Ordner setzt Windows rekursiv um, daher
/// genügt es, auf Dateien zu reagieren. Was beim Start noch losgelöst, aber vollständig daliegt (früher gescheitert), wird nachgeholt.
/// </summary>
internal sealed class PinWatcher : IDisposable
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>
    /// Kennung für Platzhalter, deren Serverfassung nicht bekannt ist (aus gewöhnlichen Dateien umgewandelt). Ohne jede Kennung lehnt Windows das
    /// Freigeben ab; der <see cref="Client.App.Sync.Hydrator"/> holt solche Dateien beim Öffnen anhand der Größe.
    /// </summary>
    public static readonly byte[] UnknownVersion = Encoding.UTF8.GetBytes("unknown");

    private readonly FileSystemWatcher _watcher;
    private readonly Action<string>? _log;

    public PinWatcher(string root, Action<string>? log)
    {
        _log = log;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += (_, e) => _ = Task.Run(() => Apply(e.FullPath, log));
        _watcher.EnableRaisingEvents = true;
        _ = Task.Run(() => Sweep(root, log));
    }

    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    public void Dispose() => _watcher.Dispose();

    /// <summary>Wendet den Pin-Zustand einer Datei an; für Ordner jede Datei darin (der Explorer meldet nicht jede Datei einzeln).</summary>
    public static void ApplyTree(string path, Action<string>? log)
    {
        if (Directory.Exists(path))
        {
            Sweep(path, log);
        }
        else
        {
            Apply(path, log);
        }
    }

    private static void Sweep(string root, Action<string>? log)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (!Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty).Equals(".syncdrive", StringComparison.OrdinalIgnoreCase))
                {
                    Apply(file, log);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(e.Message);
        }
    }

    private static void Apply(string path, Action<string>? log)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(Pinned) && IsCloudOnly(attributes))
            {
                using var handle = OpenForCloudOperation(path);
                Check(CfHydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero), $"{path} holen");
            }
            else if (attributes.HasFlag(Unpinned) && !IsCloudOnly(attributes) && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Free(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(e.Message);
        }
    }

    /// <summary>
    /// Exklusiv geöffnet (Windows verlangt es fürs Freigeben; eine geöffnete Datei bleibt also vorerst). Ein Aufruf: Kennung setzen, falls die
    /// Datei keine hat; „immer vollständig“ aufheben (hängt Windows an umgewandelte Dateien); nur wenn abgeglichen (VERIFY_IN_SYNC) freigeben.
    /// </summary>
    private static unsafe void Free(string path)
    {
        using var handle = OpenForCloudOperation(path, FileShare.None);
        var keep = HasIdentity(handle); // eine vorhandene Kennung (Inhalts-Hash) bleibt: mit ihr prüft der Abruf die Fassung
        fixed (byte* identity = UnknownVersion)
        {
            var hr = CfUpdatePlaceholder(
                handle, 0, keep ? 0 : (IntPtr)identity, keep ? 0 : (uint)UnknownVersion.Length, 0, 0,
                UpdateVerifyInSync | UpdateAllowPartial | UpdateDehydrate, 0, 0);
            Check(hr, $"{path} freigeben");
        }
    }

    /// <summary><c>CF_PLACEHOLDER_BASIC_INFO</c>: Pin-Zustand, In-Sync, zwei Datei-IDs, dann die Länge der Kennung.</summary>
    private static unsafe bool HasIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        const int MoreData = unchecked((int)0x800700EA);
        var info = stackalloc byte[512];
        var hr = CfGetPlaceholderInfo(handle, PlaceholderInfoBasic, info, 512, out _);
        return (hr >= 0 || hr == MoreData) && *(uint*)(info + 24) > 0;
    }
}
