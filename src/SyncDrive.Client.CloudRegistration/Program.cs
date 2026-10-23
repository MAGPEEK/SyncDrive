using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.UI.Notifications;

// Meldet einen Sync-Ordner bei der Windows-Shell an oder ab (StorageProviderSyncRootManager). Erst damit zeigt der Explorer Statussymbole
// und die Spalte „Status“ (ADR-10); Anheften/Freigeben bietet das SyncDrive-Menü selbst (AllowPinning aus). Der Daemon ruft dieses
// Programm auf, damit nur es die WinRT-Projektion braucht.
//
//   syncdrive-cloudreg register <aufgabe> <ordner> <anzeigename> <symbol> <version>
//   syncdrive-cloudreg unregister <aufgabe>
//   syncdrive-cloudreg notify <titel> <text> <symbol.png>   (Windows-Benachrichtigung, gleicher Grund: WinRT)
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
                AllowPinning = false, // „Immer behalten“ / „Speicherplatz freigeben“ stehen im SyncDrive-Untermenü statt oben im Explorer-Menü
                Context = CryptographicBuffer.ConvertStringToBinary(task, BinaryStringEncoding.Utf8),
            };
            StorageProviderSyncRootManager.Register(info);
            return 0;

        case ["unregister", var task]:
            StorageProviderSyncRootManager.Unregister(SyncRootId(task));
            return 0;

        case ["notify", var title, var message, var icon]:
            // Windows-Benachrichtigung im Info-Center. Eine Anwendung ohne Paket braucht dafür eine eigene Kennung (AppUserModelId) mit
            // Anzeigename und Symbol unter HKCU; die Deinstallation entfernt sie wieder.
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{NotificationAppId}"))
            {
                key.SetValue("DisplayName", "SyncDrive");
                key.SetValue("IconUri", icon);
            }

            var toast = new Windows.Data.Xml.Dom.XmlDocument();
            toast.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">"
                + $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text>"
                + "</binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier(NotificationAppId).Show(new ToastNotification(toast));
            return 0;

        case ["unregister-all"]:
            // Deinstallation: auch Sync-Roots früher gelöschter Aufgaben, aber nur die von SyncDrive für diesen Benutzer
            foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots().Where(r => r.Id.StartsWith(SyncRootId(string.Empty), StringComparison.Ordinal)))
            {
                StorageProviderSyncRootManager.Unregister(root.Id);
            }

            return 0;

        default:
            Console.Error.WriteLine("Aufruf: register <aufgabe> <ordner> <anzeigename> <symbol> <version> | unregister <aufgabe> | unregister-all | notify <titel> <text> <symbol>");
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

/// <summary>Kennung der Benachrichtigungen (AppUserModelId); dieselbe nennt die Deinstallation.</summary>
internal partial class Program
{
    private const string NotificationAppId = "SyncDrive.Client";
}
