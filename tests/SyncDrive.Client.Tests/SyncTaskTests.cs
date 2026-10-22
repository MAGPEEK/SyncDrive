using SyncDrive.Client.App.Settings;
using SyncDrive.Client.App.Tasks;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Paths;
using SyncDrive.Infra.Sqlite.Client;

namespace SyncDrive.Client.Tests;

public class SyncTaskRulesTests
{
    private static readonly string Home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "home-" + Guid.NewGuid().ToString("N")));

    private static readonly TaskEnvironment Ntfs = new(_ => true, Home, [Path.Combine(Home, "OneDrive")], CanWriteRemote: true);

    private static SyncTaskDefinition Task(string id, string local, SyncDirection direction = SyncDirection.TwoWay, bool onDemand = false) =>
        new(id, "c1", "s1", RelativePath.Root, local, direction, OnDemand: onDemand);

    [Fact]
    public void A_task_in_a_free_folder_is_valid()
    {
        var existing = new[] { Task("a", Path.Combine(Home, "MeinDrive")) };

        Assert.Empty(SyncTaskRules.Validate(Task("b", Path.Combine(Home, "Projekte")), existing, Ntfs));
    }

    [Theory]
    [InlineData("MeinDrive")]          // derselbe Ordner
    [InlineData("MeinDrive/Unter")]    // darin
    [InlineData("")]                       // darüber (Benutzerordner enthält die Aufgabe)
    [InlineData("MEINDRIVE")]          // Schreibweise egal
    public void Overlapping_local_folders_are_rejected(string relative)
    {
        var existing = new[] { Task("a", Path.Combine(Home, "MeinDrive")) };

        var violations = SyncTaskRules.Validate(Task("b", Path.Combine(Home, relative)), existing, Ntfs);

        Assert.Contains(TaskRuleViolation.LocalFolderOverlapsTask, violations);
    }

    [Fact]
    public void A_sibling_with_a_common_name_prefix_is_not_an_overlap()
    {
        Assert.False(SyncTaskRules.Overlaps(Path.Combine(Home, "Drive"), Path.Combine(Home, "Drive2")));
    }

    [Fact]
    public void Editing_a_task_does_not_conflict_with_itself()
    {
        var task = Task("a", Path.Combine(Home, "MeinDrive"));

        Assert.Empty(SyncTaskRules.Validate(task with { Direction = SyncDirection.UploadOnly }, [task], Ntfs));
    }

    [Fact]
    public void Folders_of_other_cloud_services_are_rejected()
    {
        var violations = SyncTaskRules.Validate(Task("b", Path.Combine(Home, "OneDrive", "Dokumente")), [], Ntfs);

        Assert.Contains(TaskRuleViolation.LocalFolderInOtherCloud, violations);
    }

    [Fact]
    public void At_most_64_tasks()
    {
        var existing = Enumerable.Range(0, SyncTaskRules.MaxTasks).Select(i => Task($"t{i}", Path.Combine(Home, $"Ordner{i}"))).ToList();

        Assert.Contains(TaskRuleViolation.TooManyTasks, SyncTaskRules.Validate(Task("neu", Path.Combine(Home, "Neu")), existing, Ntfs));
    }

    [Fact]
    public void Read_only_server_folders_only_allow_download_only()
    {
        var readOnly = Ntfs with { CanWriteRemote = false };

        Assert.Contains(TaskRuleViolation.ReadOnlyRequiresDownloadOnly, SyncTaskRules.Validate(Task("b", Path.Combine(Home, "X")), [], readOnly));
        Assert.Empty(SyncTaskRules.Validate(Task("b", Path.Combine(Home, "X"), SyncDirection.DownloadOnly), [], readOnly));
    }

    [Fact]
    public void On_demand_needs_ntfs_and_a_folder_below_drive_and_home_root()
    {
        var noNtfs = Ntfs with { IsNtfs = _ => false };

        Assert.Contains(TaskRuleViolation.OnDemandRequiresNtfs, SyncTaskRules.Validate(Task("b", Path.Combine(Home, "X"), onDemand: true), [], noNtfs));
        Assert.Contains(TaskRuleViolation.OnDemandNotInDriveOrHomeRoot, SyncTaskRules.Validate(Task("b", Home, onDemand: true), [], Ntfs));
        Assert.Contains(TaskRuleViolation.OnDemandNotInDriveOrHomeRoot,
            SyncTaskRules.Validate(Task("b", Path.GetPathRoot(Home)!, onDemand: true), [], Ntfs));
        Assert.Empty(SyncTaskRules.Validate(Task("b", Path.Combine(Home, "Cloud"), onDemand: true), [], Ntfs));
    }

    [Fact]
    public void Engine_config_from_a_task_uses_default_filters_and_upload_only_keep_option()
    {
        var task = Task("a", Path.Combine(Home, "X"), SyncDirection.UploadOnly) with { AdvancedConsistencyCheck = true };

        var config = App.Sync.SyncTaskConfig.From(task, new ClientSettings(), "PC");

        Assert.True(config.KeepLocallyDeletedOnServer);
        Assert.True(config.MergeOptions(startupRescan: false).IgnoreLocalDelete);
        Assert.True(config.AdvancedConsistencyCheck);
        Assert.False(config.Filter.Evaluate(RelativePath.Parse("~$a.docx"), Core.Model.NodeState.File(1, DateTimeOffset.UnixEpoch)).Allowed);
        Assert.False(App.Sync.SyncTaskConfig.From(task with { Direction = SyncDirection.TwoWay }, new ClientSettings(), "PC").KeepLocallyDeletedOnServer);
    }
}

public sealed class SqliteClientConfigStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "syncdrive-config-" + Guid.NewGuid().ToString("N"));

    public SqliteClientConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SqliteClientConfigStore Open() => new(Path.Combine(_dir, "client.db"));

    [Fact]
    public async Task Connections_tasks_and_settings_survive_reopening()
    {
        var store = Open();
        var connection = new ConnectionInfo("c1", "Mein NAS", new Uri("https://nas.example:8443/"), "anna", "d_1", "ab:cd");
        var task = new SyncTaskDefinition("t1", "c1", "sp_1", RelativePath.Parse("Projekte"), Path.Combine(_dir, "Sync"),
            SyncDirection.UploadOnly, KeepLocallyDeletedOnServer: false, OnDemand: true, AdvancedConsistencyCheck: true,
            new TaskFilterSettings(1000, ["node_modules"], ["iso"], [RelativePath.Parse("Archiv/Alt")]), Paused: true);
        var settings = new ClientSettings { Conflict = ConflictStrategy.ServerWins, Language = "de", Proxy = new ProxySettings(ProxyMode.Manual, "proxy", 3128) };
        await store.SaveConnectionAsync(connection, Ct);
        await store.SaveTaskAsync(task, Ct);
        await store.SaveSettingsAsync(settings, Ct);

        var reopened = Open();

        Assert.Equal([connection], await reopened.ListConnectionsAsync(Ct));
        var loaded = Assert.Single(await reopened.ListTasksAsync(Ct));
        Assert.Equal(task with { Filters = null }, loaded with { Filters = null });
        Assert.Equal(task.Filters!.ExcludedFolders, loaded.Filters!.ExcludedFolders);
        Assert.Equal(task.Filters.ExcludedNames, loaded.Filters.ExcludedNames);
        Assert.Equal(settings, await reopened.GetSettingsAsync(Ct));
    }

    [Fact]
    public async Task Defaults_without_saved_settings_and_removing_a_connection_removes_its_tasks()
    {
        var store = Open();
        await store.SaveConnectionAsync(new ConnectionInfo("c1", "A", new Uri("https://a/"), "u", "d", null), Ct);
        await store.SaveTaskAsync(new SyncTaskDefinition("t1", "c1", "s", RelativePath.Root, _dir), Ct);
        await store.SaveTaskAsync(new SyncTaskDefinition("t2", "c2", "s", RelativePath.Root, _dir + "2"), Ct);

        await store.RemoveConnectionAsync("c1", Ct);

        Assert.Equal(new ClientSettings(), await store.GetSettingsAsync(Ct));
        Assert.Equal(["t2"], (await store.ListTasksAsync(Ct)).Select(t => t.Id));
        Assert.Empty(await store.ListConnectionsAsync(Ct));
    }
}
