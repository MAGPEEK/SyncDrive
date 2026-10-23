using static SyncDrive.Infra.CloudFiles.NativeMethods;

namespace SyncDrive.Infra.CloudFiles;

/// <summary>
/// Setzt um, was der Nutzer im Explorer wählt: „Immer auf diesem Gerät behalten“ (angeheftet) holt den Inhalt, „Speicherplatz freigeben“
/// (losgelöst) gibt ihn frei. Windows ändert dabei nur die Attribute; handeln muss der Anbieter. Ordner setzt Windows rekursiv um, daher
/// genügt es, auf Dateien zu reagieren.
/// </summary>
internal sealed class PinWatcher : IDisposable
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

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
        _watcher.Changed += (_, e) => _ = Task.Run(() => Apply(e.FullPath));
        _watcher.EnableRaisingEvents = true;
    }

    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    public void Dispose() => _watcher.Dispose();

    private void Apply(string path)
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
            else if (attributes.HasFlag(Unpinned) && !IsCloudOnly(attributes))
            {
                // Exklusiv (Windows verlangt es fürs Freigeben; eine geöffnete Datei bleibt also vorerst). „Immer vollständig“ hängt Windows an
                // umgewandelte Dateien und lehnt dann jedes Freigeben ab: ALLOW_PARTIAL hebt das auf. VERIFY_IN_SYNC: nur Abgeglichenes wird frei.
                using var handle = OpenForCloudOperation(path, FileShare.None);
                Check(
                    CfUpdatePlaceholder(handle, 0, 0, 0, 0, 0, UpdateVerifyInSync | UpdateAllowPartial | UpdateDehydrate, 0, 0),
                    $"{path} freigeben");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke(e.Message);
        }
    }
}
