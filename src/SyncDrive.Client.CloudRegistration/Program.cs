using System.Security.Principal;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

// Meldet einen Sync-Ordner bei der Windows-Shell an oder ab (StorageProviderSyncRootManager). Erst damit zeigt der Explorer Statussymbole,
// die Spalte „Status“ und die Einträge „Immer auf diesem Gerät beibehalten“ / „Speicherplatz freigeben“ (ADR-10). Der Daemon ruft dieses
// Programm auf, damit nur es die WinRT-Projektion braucht.
//
//   syncdrive-cloudreg register <aufgabe> <ordner> <anzeigename> <symbol> <version>
//   syncdrive-cloudreg unregister <aufgabe>
//
// Rückgabe 0 bei Erfolg, sonst 1 mit Meldung auf stderr.
try
{
    switch (args)
    {
        case ["register", var task, var path, var name, var icon, var version]:
            var info = new StorageProviderSyncRootInfo
            {
                Id = SyncRootId(task),
                Path = await StorageFolder.GetFolderFromPathAsync(path),
                DisplayNameResource = name,
                IconResource = icon,
                Version = version,
                HydrationPolicy = StorageProviderHydrationPolicy.Full,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
                PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
                InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.FileReadOnlyAttribute
                    | StorageProviderInSyncPolicy.FileHiddenAttribute | StorageProviderInSyncPolicy.FileSystemAttribute
                    | StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryCreationTime
                    | StorageProviderInSyncPolicy.DirectoryReadOnlyAttribute | StorageProviderInSyncPolicy.DirectoryHiddenAttribute
                    | StorageProviderInSyncPolicy.DirectorySystemAttribute | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                ShowSiblingsAsGroup = false,
                Context = CryptographicBuffer.ConvertStringToBinary(task, BinaryStringEncoding.Utf8),
            };
            StorageProviderSyncRootManager.Register(info);
            return 0;

        case ["unregister", var task]:
            StorageProviderSyncRootManager.Unregister(SyncRootId(task));
            return 0;

        default:
            Console.Error.WriteLine("Aufruf: register <aufgabe> <ordner> <anzeigename> <symbol> <version> | unregister <aufgabe>");
            return 2;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message}");
    return 1;
}

// Schema der Shell: Anbieter!SID!Konto. Die Aufgaben-ID macht jeden Sync-Ordner eindeutig.
static string SyncRootId(string task) => $"SyncDrive!{WindowsIdentity.GetCurrent().User!.Value}!{task}";
