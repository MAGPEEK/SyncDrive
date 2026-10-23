using SyncDrive.Client.App.Settings;
using SyncDrive.Core.Filtering;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Client.Tests;

public class DefaultFiltersTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;

    private static FilterDecision File(string path, long size = 10, TaskFilterSettings? task = null) =>
        DefaultFilters.Create(task).Evaluate(RelativePath.Parse(path), NodeState.File(size, T));

    private static FilterDecision Dir(string path) =>
        DefaultFilters.Create().Evaluate(RelativePath.Parse(path), NodeState.Directory(T));

    [Theory]
    [InlineData("~$Bericht.docx", FilterReason.Prefix)]
    [InlineData("Ordner/~WRL0001.tmp", FilterReason.Prefix)]
    [InlineData(".gitkeep", FilterReason.Prefix)]
    [InlineData("desktop.ini", FilterReason.Name)]
    [InlineData("Bilder/Thumbs.db", FilterReason.Name)]
    [InlineData("Projekt/@eaDir/x.jpg", FilterReason.Name)]
    [InlineData("#recycle/alt.txt", FilterReason.Name)]
    [InlineData("Verknüpfung.lnk", FilterReason.Extension)]
    [InlineData("mail.PST", FilterReason.Extension)]
    [InlineData("datei.swp", FilterReason.Extension)]
    [InlineData("tmp", FilterReason.Name)]
    public void Files_the_original_never_syncs_are_rejected_with_their_reason(string path, FilterReason reason)
    {
        Assert.Equal(FilterDecision.Reject(reason), File(path));
    }

    [Theory]
    [InlineData("Bericht.docx")]
    [InlineData("Ordner/Unterordner/Foto.jpg")]
    [InlineData("tmp/datei.txt")]            // ein Ordner namens tmp ist erlaubt, nur die Datei „tmp“ nicht
    [InlineData("Version~2.txt")]            // ~ nur am Anfang
    public void Normal_files_are_allowed(string path)
    {
        Assert.True(File(path).Allowed);
    }

    [Fact]
    public void Hidden_folders_are_allowed_but_system_folders_are_not()
    {
        Assert.True(Dir(".config").Allowed);
        Assert.False(Dir("System Volume Information").Allowed);
    }

    [Theory]
    [InlineData(".Trashes")]
    [InlineData(".fseventsd")]
    [InlineData(".Spotlight-V100")]
    [InlineData(".TemporaryItems")]
    [InlineData(".DocumentRevisions-V100")]
    public void Folders_macos_manages_itself_are_never_synced(string folder)
    {
        Assert.Equal(FilterDecision.Reject(FilterReason.Name), Dir(folder));
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("Fotos/._Urlaub.jpg")] // AppleDouble: Zusatzdaten einer Datei auf fremden Dateisystemen
    [InlineData(".localized")]
    public void Finder_helper_files_are_never_synced(string path)
    {
        Assert.False(File(path).Allowed);
    }

    [Fact]
    public void Paths_longer_than_247_characters_are_rejected()
    {
        var path = string.Join('/', Enumerable.Repeat(new string('a', 49), 5)) + "/x.txt"; // 251 Zeichen

        Assert.Equal(FilterDecision.Reject(FilterReason.PathLength), File(path));
    }

    [Fact]
    public void Default_size_limit_is_64_gib_and_a_task_limit_applies_when_smaller()
    {
        Assert.True(File("gross.bin", DefaultFilters.DefaultMaxFileSize).Allowed);
        Assert.False(File("riesig.bin", DefaultFilters.DefaultMaxFileSize + 1).Allowed);
        Assert.Equal(FilterDecision.Reject(FilterReason.Size), File("film.mkv", 2_000, new TaskFilterSettings(MaxFileSizeBytes: 1_000)));
    }

    [Fact]
    public void Task_filters_for_names_extensions_and_selective_sync_are_added()
    {
        var task = new TaskFilterSettings(
            ExcludedNames: ["node_modules"], ExcludedExtensions: [".iso"], ExcludedFolders: [RelativePath.Parse("Archiv")]);

        Assert.Equal(FilterReason.Name, File("web/node_modules/x.js", task: task).Reason);
        Assert.Equal(FilterReason.Extension, File("image.ISO", task: task).Reason);
        Assert.Equal(FilterReason.SelectiveSync, File("Archiv/2020/a.txt", task: task).Reason);
    }
}
