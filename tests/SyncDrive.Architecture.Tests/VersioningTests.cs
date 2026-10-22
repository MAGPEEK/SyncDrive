using System.Text.RegularExpressions;
using System.Xml.Linq;
using SyncDrive.Contracts;

namespace SyncDrive.Architecture.Tests;

/// <summary>Versionierung (Semantic Versioning): eine Quelle im Build, oberster CHANGELOG-Eintrag gleich, Einträge absteigend.</summary>
public sealed partial class VersioningTests
{
    private static readonly string BuildRoot = FindUp("Directory.Build.props");

    private static string PropsVersion =>
        XDocument.Load(Path.Combine(BuildRoot, "Directory.Build.props")).Descendants("Version").Single().Value;

    private static IReadOnlyList<Version> ChangelogVersions =>
    [
        .. ChangelogEntry().Matches(File.ReadAllText(Path.Combine(FindUp("CHANGELOG.md"), "CHANGELOG.md"))).Select(m => Version.Parse(m.Groups[1].Value)),
    ];

    [Fact]
    public void The_version_is_semantic_and_the_one_every_assembly_carries()
    {
        Assert.Matches(SemVer(), PropsVersion);
        Assert.Equal(PropsVersion, ProductVersion.Current);
    }

    [Fact]
    public void The_changelog_starts_with_the_current_version_and_runs_backwards()
    {
        var versions = ChangelogVersions;

        Assert.Equal(Version.Parse(PropsVersion), versions[0]);
        Assert.Equal(versions.OrderDescending(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    private static string FindUp(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, file)))
            {
                return dir.FullName;
            }
        }

        throw new FileNotFoundException(file);
    }

    [GeneratedRegex(@"^## \[(\d+\.\d+\.\d+)\]", RegexOptions.Multiline)]
    private static partial Regex ChangelogEntry();

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex SemVer();
}
