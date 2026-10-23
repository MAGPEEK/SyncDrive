using System.Runtime.InteropServices;
using System.Text;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;
using static SyncDrive.Infra.CloudFiles.NativeMethods;

namespace SyncDrive.Infra.CloudFiles;

/// <summary>
/// Sync-Ordner als Sync-Root der Cloud-Files-API (ADR-10): Windows zeigt Statussymbole und die Spalte „Status“ und fragt beim Öffnen eines
/// Platzhalters nach dem Inhalt; „Immer auf diesem Gerät behalten“ und „Speicherplatz freigeben“ stehen im SyncDrive-Menü (<see cref="SetPinnedAsync"/>). Inhalte werden vollständig
/// geholt (<c>HYDRATION_FULL</c>), alle Platzhalter legt der Sync selbst an (<c>POPULATION_ALWAYS_FULL</c>), die Registrierung gilt nur für
/// den angemeldeten Benutzer und braucht kein Adminrecht.
/// Symbole, Statusspalte und Menüeinträge zeigt der Explorer nur für Ordner, die auch bei der Shell angemeldet sind
/// (<c>StorageProviderSyncRootManager</c>, WinRT). Das übernimmt das Hilfsprogramm <c>syncdrive-cloudreg</c>; fehlt es, bleibt es bei der
/// Anmeldung im Dateisystem: Platzhalter und Abruf funktionieren, nur ohne Symbole.
/// </summary>
/// <param name="shellRegistration">Pfad zu <c>syncdrive-cloudreg.exe</c>.</param>
/// <param name="iconResource">Symbol im Explorer, z. B. <c>C:\…\syncdrive.exe,0</c>.</param>
public sealed class WindowsCloudFolders(
    string providerVersion, Action<string>? log = null, string? shellRegistration = null, string? iconResource = null) : ICloudFolders
{
    private static readonly Guid ProviderId = new("6b1f3c52-8f0e-4d0a-9a51-2f7c1e5d9b34");

    /// <summary>Ab Windows 10 1809 (Blockieren des eigenen impliziten Abrufs).</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public async Task<CloudFolder> AttachAsync(SyncTaskDefinition task, Hydrator hydrator, CancellationToken ct)
    {
        var root = Root(task);
        if (!await RunShellRegistrationAsync(ct, "register", task.Id, root, "SyncDrive", iconResource ?? string.Empty, providerVersion))
        {
            Register(root, task.Id);
        }

        var connection = CloudConnection.Connect(root, hydrator, log);
        var pins = new PinWatcher(root, log);
        return new CloudFolder(new WindowsPlaceholders(root), new Both(pins, connection));
    }

    public async Task DetachAsync(SyncTaskDefinition task, CancellationToken ct)
    {
        var root = Root(task);
        if (Directory.Exists(root))
        {
            Revert(root, ct);
        }

        if (!await RunShellRegistrationAsync(ct, "unregister", task.Id))
        {
            var hr = CfUnregisterSyncRoot(root);
            if (hr < 0)
            {
                log?.Invoke($"Sync-Root {root} abmelden: 0x{hr:X8}"); // war nie registriert oder ist schon weg
            }
        }
    }

    /// <summary>
    /// Setzt den Pin-Zustand (bei Ordnern rekursiv) und holt oder gibt gleich frei. Auf die Attributänderung allein ist kein Verlass: Wählt man
    /// dasselbe noch einmal (nach einem gescheiterten Versuch), ändert sich kein Attribut und der <see cref="PinWatcher"/> erfährt nichts.
    /// </summary>
    public Task SetPinnedAsync(string localPath, bool keep, CancellationToken ct)
    {
        using (var handle = OpenForCloudOperation(localPath))
        {
            var recurse = Directory.Exists(localPath) ? SetPinRecurse : 0;
            Check(CfSetPinState(handle, keep ? PinStatePinned : PinStateUnpinned, recurse, IntPtr.Zero), $"{localPath} anheften");
        }

        _ = Task.Run(() => PinWatcher.ApplyTree(localPath, log), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Ruft <c>syncdrive-cloudreg</c> auf; false, wenn es fehlt oder scheitert (dann gilt der Weg über das Dateisystem).</summary>
    private async Task<bool> RunShellRegistrationAsync(CancellationToken ct, params string[] arguments)
    {
        if (shellRegistration is null || !File.Exists(shellRegistration))
        {
            return false;
        }

        var start = new System.Diagnostics.ProcessStartInfo(shellRegistration)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            log?.Invoke($"Shell-Anmeldung ({arguments[0]}) gescheitert: {error.Trim()}");
        }

        return process.ExitCode == 0;
    }

    private static string Root(SyncTaskDefinition task) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.LocalPath));

    private unsafe void Register(string root, string taskId)
    {
        var identity = Encoding.UTF8.GetBytes(taskId);
        var name = Marshal.StringToHGlobalUni("SyncDrive");
        var version = Marshal.StringToHGlobalUni(providerVersion);
        try
        {
            fixed (byte* id = identity)
            {
                var registration = new SyncRegistration
                {
                    StructSize = (uint)sizeof(SyncRegistration),
                    ProviderName = name,
                    ProviderVersion = version,
                    SyncRootIdentity = (IntPtr)id,
                    SyncRootIdentityLength = (uint)identity.Length,
                    ProviderId = ProviderId,
                };
                var policies = new SyncPolicies
                {
                    StructSize = (uint)sizeof(SyncPolicies),
                    HydrationPrimary = HydrationFull,
                    PopulationPrimary = PopulationAlwaysFull,
                    InSync = InSyncTrackAll,
                };

                // Erst als Aktualisierung (Neustart des Daemons), sonst neu.
                var hr = CfRegisterSyncRoot(root, registration, policies, RegisterUpdate | RegisterMarkInSyncOnRoot);
                if (hr < 0)
                {
                    hr = CfRegisterSyncRoot(root, registration, policies, RegisterMarkInSyncOnRoot);
                }

                Check(hr, $"Sync-Root {root} registrieren");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(version);
        }
    }

    /// <summary>Platzhalter ohne Inhalt löschen (der Inhalt liegt auf dem Server), alle anderen wieder zu gewöhnlichen Dateien und Ordnern machen.</summary>
    private void Revert(string root, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(file);
                if (PinWatcher.IsCloudOnly(attributes))
                {
                    File.Delete(file);
                }
                else if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    RevertOne(file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"{file} zurückwandeln: {e.Message}");
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(root))
        {
            try
            {
                RevertOne(directory);
            }
            catch (IOException e)
            {
                log?.Invoke($"{directory} zurückwandeln: {e.Message}");
            }
        }
    }

    private static void RevertOne(string path)
    {
        using var handle = OpenForCloudOperation(path);
        var hr = CfRevertPlaceholder(handle, 0, IntPtr.Zero);
        if (hr != NotACloudFile)
        {
            Check(hr, $"{path} zurückwandeln");
        }
    }

    private sealed class Both(IDisposable pins, IAsyncDisposable connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            pins.Dispose();
            await connection.DisposeAsync();
        }
    }
}

/// <summary>Platzhalter eines Sync-Roots über die Cloud-Files-API.</summary>
internal sealed class WindowsPlaceholders(string root) : IPlaceholders
{
    public unsafe Task CreateAsync(RelativePath path, long size, DateTimeOffset mtime, ContentHash hash, CancellationToken ct)
    {
        var full = Full(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var identity = Encoding.UTF8.GetBytes(hash.ToString());
        var time = mtime.UtcDateTime.ToFileTimeUtc();
        fixed (char* name = Path.GetFileName(full))
        fixed (byte* id = identity)
        {
            var info = new PlaceholderCreateInfo
            {
                RelativeFileName = (IntPtr)name,
                CreationTime = time,
                LastAccessTime = time,
                LastWriteTime = time,
                ChangeTime = time,
                FileAttributes = FileAttributeNormal,
                FileSize = size,
                FileIdentity = (IntPtr)id,
                FileIdentityLength = (uint)identity.Length,
                Flags = PlaceholderMarkInSync | PlaceholderSupersede,
            };
            Check(CfCreatePlaceholders(directory, &info, 1, 0, out _), $"Platzhalter {path} anlegen");
            Check(info.Result, $"Platzhalter {path} anlegen");
        }

        return Task.CompletedTask;
    }

    public Task SetInSyncAsync(RelativePath path, bool inSync, CancellationToken ct)
    {
        var full = Full(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return Task.CompletedTask;
        }

        using var handle = OpenForCloudOperation(full);
        var hr = CfSetInSyncState(handle, inSync ? InSyncStateInSync : InSyncStateNotInSync, 0, IntPtr.Zero);
        if (hr == NotACloudFile)
        {
            // Gewöhnliche Datei (neu angelegt oder vom Sync vollständig geholt): zum Platzhalter mit Inhalt machen; nicht abgeglichen bleibt sie so.
            // Dateien brauchen eine Kennung, sonst lässt Windows sie später nicht freigeben (Ordner nicht).
            hr = inSync ? Convert(handle, File.Exists(full)) : 0;
        }

        Check(hr, $"Status von {path} setzen");
        return Task.CompletedTask;
    }

    private static unsafe int Convert(Microsoft.Win32.SafeHandles.SafeFileHandle handle, bool isFile)
    {
        fixed (byte* identity = PinWatcher.UnknownVersion)
        {
            return isFile
                ? CfConvertToPlaceholder(handle, (IntPtr)identity, (uint)PinWatcher.UnknownVersion.Length, ConvertMarkInSync, IntPtr.Zero, IntPtr.Zero)
                : CfConvertToPlaceholder(handle, IntPtr.Zero, 0, ConvertMarkInSync, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private string Full(RelativePath path) =>
        path.IsRoot ? root : Path.Combine(root, path.Value.Replace('/', Path.DirectorySeparatorChar));
}
