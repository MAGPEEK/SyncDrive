using Microsoft.Win32;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.Ui;

namespace SyncDrive.Client.Daemon;

/// <summary>Schreibzugriff auf <c>HKCU\Software\Classes</c>; austauschbar, damit Tests nichts im echten Explorer eintragen.</summary>
public interface IRegistryWriter
{
    void SetValue(string key, string? name, string value);

    void DeleteTree(string key);
}

/// <summary>Echte Registry des angemeldeten Benutzers (kein Adminrecht nötig, nur für ihn sichtbar).</summary>
public sealed class CurrentUserClasses : IRegistryWriter
{
    private const string Root = @"Software\Classes\";

    public void SetValue(string key, string? name, string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = Registry.CurrentUser.CreateSubKey(Root + key);
        k.SetValue(name, value);
    }

    public void DeleteTree(string key)
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(Root + key, throwOnMissingSubKey: false);
        }
    }
}

/// <summary>
/// Explorer-Menü „SyncDrive“: <b>ein</b> Eintrag mit Untermenü, nur in Sync-Ordnern (<c>AppliesTo</c>), Texte in der
/// Sprache des Clients. Als statische Verben unter HKCU, nicht als COM-Erweiterung: kein fremder Code im Explorer-Prozess, kein Adminrecht,
/// rückstandsfrei entfernbar (ADR-10). Jeder Eintrag startet <c>syncdrive.exe --shell &lt;aktion&gt; "%1"</c>.
/// </summary>
public sealed class ShellMenuRegistrar(IRegistryWriter registry, IClientConfigStore config, string trayExecutable) : IShellIntegration
{
    public const string FileKey = @"*\shell\SyncDrive";
    public const string FolderKey = @"Directory\shell\SyncDrive";

    public async Task RefreshAsync(CancellationToken ct)
    {
        var settings = await config.GetSettingsAsync(ct);
        var tasks = await config.ListTasksAsync(ct);
        registry.DeleteTree(FileKey);
        registry.DeleteTree(FolderKey);
        if (!settings.ShowContextMenu || tasks.Count == 0)
        {
            return;
        }

        var texts = Texts.For(settings.Language);
        var appliesTo = string.Join(" OR ", tasks.Select(t => $"System.ItemPathDisplay:~<\"{t.LocalPath.TrimEnd('\\', '/')}\\\""));
        Register(FileKey, appliesTo, texts.AppName,
        [
            ("01versions", texts.ShellVersions, "versions"),
        ]);
        Register(FolderKey, appliesTo, texts.AppName,
        [
            ("01stopsync", texts.ShellStopSync, "stopsync"),
            ("02resumesync", texts.ShellResumeSync, "resumesync"),
        ]);
    }

    /// <summary>Beim Deinstallieren oder wenn der Daemon ohne Aufgaben endet: alles entfernen.</summary>
    public void Remove()
    {
        registry.DeleteTree(FileKey);
        registry.DeleteTree(FolderKey);
    }

    private void Register(string key, string appliesTo, string title, IReadOnlyList<(string Key, string Text, string Verb)> entries)
    {
        registry.SetValue(key, "MUIVerb", title);
        registry.SetValue(key, "Icon", $"\"{trayExecutable}\",0");
        registry.SetValue(key, "SubCommands", string.Empty); // Untermenü aus dem eigenen shell-Unterschlüssel
        registry.SetValue(key, "AppliesTo", appliesTo);
        foreach (var (entryKey, text, verb) in entries)
        {
            registry.SetValue($@"{key}\shell\{entryKey}", "MUIVerb", text);
            registry.SetValue($@"{key}\shell\{entryKey}\command", null, $"\"{trayExecutable}\" --shell {verb} \"%1\"");
        }
    }
}
