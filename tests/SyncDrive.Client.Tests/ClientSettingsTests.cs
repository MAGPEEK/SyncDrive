using SyncDrive.Client.App.Settings;
using SyncDrive.Core.Merge;

namespace SyncDrive.Client.Tests;

public class ClientSettingsTests
{
    [Fact]
    public void Defaults_match_the_observed_defaults_of_the_original()
    {
        var s = new ClientSettings();

        Assert.True(s.StartWithWindows);
        Assert.Equal(OnRestartLocalDeletes.Refetch, s.OnRestart);
        Assert.Equal(ConflictStrategy.NewerWins, s.Conflict);
        Assert.True(s.RenameDiscarded);
        Assert.True(s.ShowDesktopNotifications);
        Assert.True(s.ShowOverlays && s.ShowOverlaysForUnsynced && s.ShowContextMenu);
        Assert.Equal(ProxyMode.None, s.Proxy.Mode);
    }

    [Theory]
    [InlineData(ConflictStrategy.NewerWins, true, ConflictPolicy.NewerWins, true)]
    [InlineData(ConflictStrategy.ServerWins, false, ConflictPolicy.ServerWins, false)]
    public void Conflict_choice_maps_to_merge_policy(ConflictStrategy strategy, bool rename, ConflictPolicy policy, bool expectedRename)
    {
        var options = new ClientSettings { Conflict = strategy, RenameDiscarded = rename }.ToMergeOptions(SyncDirection.TwoWay, startupRescan: false);

        Assert.Equal((policy, expectedRename), (options.Policy, options.RenameConflict));
    }

    [Theory]
    [InlineData(OnRestartLocalDeletes.Refetch, true, true)]
    [InlineData(OnRestartLocalDeletes.Refetch, false, false)]
    [InlineData(OnRestartLocalDeletes.PropagateDelete, true, false)]
    public void Local_deletes_are_only_ignored_on_startup_when_refetch_is_chosen(OnRestartLocalDeletes onRestart, bool startup, bool ignored)
    {
        var options = new ClientSettings { OnRestart = onRestart }.ToMergeOptions(SyncDirection.TwoWay, startup);

        Assert.Equal(ignored, options.IgnoreLocalDelete);
    }
}
