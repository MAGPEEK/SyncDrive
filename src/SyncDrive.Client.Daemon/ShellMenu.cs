using Microsoft.Win32;
using SyncDrive.Client.App.Control;
using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Tasks;
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
        var appliesTo = AppliesTo(tasks);
        var onDemand = tasks.Where(t => t.OnDemand).ToList();
        IReadOnlyList<MenuEntry> pins = onDemand.Count == 0
            ? []
            : [new("keep", texts.ShellKeep, "keep", AppliesTo(onDemand)), new("freeup", texts.ShellFreeUp, "freeup", AppliesTo(onDemand))];
        Register(FileKey, appliesTo, texts.AppName, [new("versions", texts.ShellVersions, "versions"), .. pins]);
        Register(FolderKey, appliesTo, texts.AppName,
            [.. pins, new("stopsync", texts.ShellStopSync, "stopsync"), new("resumesync", texts.ShellResumeSync, "resumesync")]);
    }

    /// <summary>Nur innerhalb der Sync-Ordner dieser Aufgaben.</summary>
    private static string AppliesTo(IEnumerable<SyncTaskDefinition> tasks) =>
        string.Join(" OR ", tasks.Select(t => $"System.ItemPathDisplay:~<\"{t.LocalPath.TrimEnd('\\', '/')}\\\""));

    /// <param name="AppliesTo">Engere Einschränkung für diesen Eintrag (z. B. nur Aufgaben mit Dateien auf Abruf); sonst gilt die des Menüs.</param>
    private sealed record MenuEntry(string Name, string Text, string Verb, string? AppliesTo = null);

    /// <summary>Beim Deinstallieren oder wenn der Daemon ohne Aufgaben endet: alles entfernen.</summary>
    public void Remove()
    {
        registry.DeleteTree(FileKey);
        registry.DeleteTree(FolderKey);
    }

    /// <summary>Einträge in dieser Reihenfolge (Schlüssel <c>01name</c>, <c>02name</c> …; der Explorer sortiert nach Schlüssel).</summary>
    private void Register(string key, string appliesTo, string title, IReadOnlyList<MenuEntry> entries)
    {
        registry.SetValue(key, "MUIVerb", title);
        registry.SetValue(key, "Icon", $"\"{trayExecutable}\",0");
        registry.SetValue(key, "SubCommands", string.Empty); // Untermenü aus dem eigenen shell-Unterschlüssel
        registry.SetValue(key, "AppliesTo", appliesTo);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var entryKey = $@"{key}\shell\{i + 1:00}{entry.Name}";
            registry.SetValue(entryKey, "MUIVerb", entry.Text);
            if (entry.AppliesTo is { } narrower)
            {
                registry.SetValue(entryKey, "AppliesTo", narrower);
            }

            registry.SetValue($@"{entryKey}\command", null, $"\"{trayExecutable}\" --shell {entry.Verb} \"%1\"");
        }
    }
}
