using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SyncDrive.Client.Daemon;

/// <summary>Aufräumen beim Deinstallieren unter Windows, alles unter <c>HKCU</c> des angemeldeten Benutzers.</summary>
/// <param name="cloudRegistration">Pfad zu <c>syncdrive-cloudreg.exe</c> (meldet übrige Sync-Roots bei der Shell ab).</param>
[SupportedOSPlatform("windows")]
public sealed class WindowsUninstallSteps(ShellMenuRegistrar menu, WindowsAutostart autostart, string? cloudRegistration) : IUninstallSteps
{
    private const string Clsid = @"Software\Classes\CLSID";
    private const string NameSpace = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    private const string NewStartPanel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    public void RemoveExplorerMenu() => menu.Remove();

    public void RemoveAutostart() => autostart.Apply(false);

    public async Task UnregisterRemainingSyncRootsAsync(CancellationToken ct)
    {
        if (cloudRegistration is null || !File.Exists(cloudRegistration))
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo(cloudRegistration, "unregister-all") { UseShellExecute = false, CreateNoWindow = true })!;
        await process.WaitForExitAsync(ct);
    }

    /// <summary>
    /// Cloud-Ordner im Navigationsbereich legt Windows als CLSID mit Anzeigenamen und Zielordner an, in der 64- <b>und</b> der 32-Bit-Ansicht
    /// der Registry; beim Abmelden verschwindet nur die 64-Bit-Kopie. Entfernt werden in beiden Ansichten nur Einträge mit Namen „SyncDrive“
    /// und Zielordner, samt Verweis im Navigationsbereich.
    /// </summary>
    public int RemoveNavigationEntries()
    {
        var removed = 0;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var classes = user.OpenSubKey(Clsid);
            foreach (var id in classes?.GetSubKeyNames().Where(id => IsSyncDriveFolder(classes, id)).ToList() ?? [])
            {
                user.DeleteSubKeyTree($@"{Clsid}\{id}", throwOnMissingSubKey: false);
                user.DeleteSubKeyTree($@"{NameSpace}\{id}", throwOnMissingSubKey: false);
                using var panel = user.OpenSubKey(NewStartPanel, writable: true);
                panel?.DeleteValue(id, throwOnMissingValue: false);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Einstellungen und die Kennung der Windows-Benachrichtigungen (legt <c>syncdrive-cloudreg notify</c> an).</summary>
    public void RemoveSettingsKey()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\SyncDrive", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\SyncDrive.Client", throwOnMissingSubKey: false);
    }

    private static bool IsSyncDriveFolder(RegistryKey classes, string id)
    {
        using var entry = classes.OpenSubKey(id);
        using var bag = entry?.OpenSubKey(@"Instance\InitPropertyBag");
        return entry?.GetValue(null) as string == "SyncDrive" && bag?.GetValue("TargetFolderPath") is string;
    }
}
