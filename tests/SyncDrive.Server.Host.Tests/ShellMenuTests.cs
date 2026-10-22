using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Client.Daemon;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.Sqlite.Client;

namespace SyncDrive.Server.Host.Tests;

/// <summary>Explorer-Menü als Registry-Einträge, geprüft gegen eine Registry im Speicher (der echte Explorer bleibt unberührt).</summary>
public sealed class ShellMenuTests : IDisposable
{
    private const string Exe = @"C:\Programme\SyncDrive\syncdrive.exe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncdrive-shellmenu-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteClientConfigStore _config;
    private readonly MemoryRegistry _registry = new();

    public ShellMenuTests()
    {
        Directory.CreateDirectory(_root);
        _config = new SqliteClientConfigStore(Path.Combine(_root, "client.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private ShellMenuRegistrar Registrar() => new(_registry, _config, Exe);

    [Fact]
    public async Task One_entry_with_a_submenu_appears_only_in_sync_folders_and_runs_the_app()
    {
        await _config.SaveTaskAsync(new SyncTaskDefinition("t1", "c1", "s1", RelativePath.Root, @"C:\Users\x\SyncDrive\"), default);
        await _config.SaveTaskAsync(new SyncTaskDefinition("t2", "c1", "s2", RelativePath.Root, @"D:\Fotos"), default);

        await Registrar().RefreshAsync(default);

        var file = ShellMenuRegistrar.FileKey;
        Assert.Equal("SyncDrive", _registry.Get(file, "MUIVerb"));
        Assert.Equal(string.Empty, _registry.Get(file, "SubCommands"));
        Assert.Equal($"\"{Exe}\",0", _registry.Get(file, "Icon"));
        Assert.Equal(
            "System.ItemPathDisplay:~<\"C:\\Users\\x\\SyncDrive\\\" OR System.ItemPathDisplay:~<\"D:\\Fotos\\\"",
            _registry.Get(file, "AppliesTo"));
        Assert.Equal("Browse previous versions", _registry.Get(file + @"\shell\01versions", "MUIVerb"));
        Assert.Equal($"\"{Exe}\" --shell versions \"%1\"", _registry.Get(file + @"\shell\01versions\command", null));
        Assert.Equal(["01versions"], _registry.Children(file + @"\shell"));
        Assert.Equal(["01stopsync", "02resumesync"], _registry.Children(ShellMenuRegistrar.FolderKey + @"\shell"));
    }

    [Fact]
    public async Task Keep_and_free_up_sit_in_the_submenu_only_for_tasks_with_files_on_demand()
    {
        await _config.SaveTaskAsync(new SyncTaskDefinition("t1", "c1", "s1", RelativePath.Root, @"C:\Sync"), default);
        await _config.SaveTaskAsync(new SyncTaskDefinition("t2", "c1", "s2", RelativePath.Root, @"D:\Abruf", OnDemand: true), default);
        await _config.SaveSettingsAsync(new ClientSettings { Language = "de" }, default);

        await Registrar().RefreshAsync(default);

        var file = ShellMenuRegistrar.FileKey + @"\shell";
        var folder = ShellMenuRegistrar.FolderKey + @"\shell";
        Assert.Equal(["01versions", "02keep", "03freeup"], _registry.Children(file));
        Assert.Equal(["01keep", "02freeup", "03stopsync", "04resumesync"], _registry.Children(folder));
        Assert.Equal("Immer auf diesem Gerät behalten", _registry.Get(file + @"\02keep", "MUIVerb"));
        Assert.Equal("Speicherplatz freigeben", _registry.Get(folder + @"\02freeup", "MUIVerb"));
        Assert.Equal("System.ItemPathDisplay:~<\"D:\\Abruf\\\"", _registry.Get(file + @"\03freeup", "AppliesTo"));
        Assert.Null(_registry.Get(file + @"\01versions", "AppliesTo"));
        Assert.Equal($"\"{Exe}\" --shell keep \"%1\"", _registry.Get(folder + @"\01keep\command", null));
    }

    [Fact]
    public async Task The_menu_follows_the_language_and_disappears_when_switched_off_or_without_tasks()
    {
        await _config.SaveTaskAsync(new SyncTaskDefinition("t1", "c1", "s1", RelativePath.Root, @"C:\Sync"), default);
        await _config.SaveSettingsAsync(new ClientSettings { Language = "de" }, default);
        await Registrar().RefreshAsync(default);
        Assert.Equal("Diesen Ordner nicht mehr synchronisieren", _registry.Get(ShellMenuRegistrar.FolderKey + @"\shell\01stopsync", "MUIVerb"));

        await _config.SaveSettingsAsync(new ClientSettings { ShowContextMenu = false }, default);
        await Registrar().RefreshAsync(default);
        Assert.Empty(_registry.Keys);

        await _config.SaveSettingsAsync(new ClientSettings(), default);
        await _config.RemoveTaskAsync("t1", default);
        await Registrar().RefreshAsync(default);
        Assert.Empty(_registry.Keys);
    }

    private sealed class MemoryRegistry : IRegistryWriter
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Keys => _keys.Keys;

        public void SetValue(string key, string? name, string value)
        {
            if (!_keys.TryGetValue(key, out var values))
            {
                _keys[key] = values = [];
            }

            values[name ?? string.Empty] = value;
        }

        public void DeleteTree(string key)
        {
            foreach (var existing in _keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _keys.Remove(existing);
            }
        }

        public string? Get(string key, string? name) => _keys.GetValueOrDefault(key)?.GetValueOrDefault(name ?? string.Empty);

        public List<string> Children(string key) =>
            [.. _keys.Keys.Where(k => k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase) && !k[(key.Length + 1)..].Contains('\\', StringComparison.Ordinal))
                .Select(k => k[(key.Length + 1)..]).Order(StringComparer.Ordinal)];
    }
}
