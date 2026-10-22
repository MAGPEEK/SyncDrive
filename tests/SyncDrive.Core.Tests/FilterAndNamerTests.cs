using SyncDrive.Core.Filtering;
using SyncDrive.Core.Merge;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Core.Tests;

public class FilterEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static NodeState File(long size = 1) => NodeState.File(size, T0);

    private static FilterDecision Eval(IFilterRule rule, string path, NodeState? state = null) =>
        new FilterEngine([rule]).Evaluate(RelativePath.Parse(path), state ?? File());

    [Fact]
    public void No_rules_allow_everything() =>
        Assert.True(new FilterEngine([]).Evaluate(RelativePath.Parse("a"), File()).Allowed);

    [Theory]
    [InlineData("a/b.TMP", true)]
    [InlineData("a/b.tmp", true)]
    [InlineData("a/b.txt", false)]
    [InlineData("a/tmp", false)]
    [InlineData("a/.tmp", false)] // Dateiname ohne Stamm, kein Suffix
    public void Extension_rule(string path, bool rejected)
    {
        var decision = Eval(new ExtensionRule([".tmp"]), path);
        Assert.Equal(rejected, !decision.Allowed);
        Assert.Equal(rejected ? FilterReason.Extension : null, decision.Reason);
    }

    [Fact]
    public void Extension_rule_ignores_directories() =>
        Assert.True(Eval(new ExtensionRule(["tmp"]), "a.tmp", NodeState.Directory(T0)).Allowed);

    [Theory]
    [InlineData("node_modules", true)]
    [InlineData("src/Node_Modules/x.js", true)]
    [InlineData("src/modules/x.js", false)]
    public void Name_rule_covers_the_whole_subtree(string path, bool rejected) =>
        Assert.Equal(rejected, !Eval(new NameRule(["node_modules"]), path).Allowed);

    [Theory]
    [InlineData("*.bak", "a/b.bak", true)]
    [InlineData("*.bak", "a/b.bakx", false)]
    [InlineData("~$*", "a/~$doc.docx", true)]
    [InlineData("a/*.bak", "a/b.bak", true)]
    [InlineData("a/*.bak", "a/c/b.bak", false)]
    [InlineData("a/**/*.bak", "a/c/d/b.bak", true)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file10.txt", false)]
    [InlineData("a.b", "axb", false)] // Punkt ist wörtlich
    public void Glob_rule(string pattern, string path, bool rejected) =>
        Assert.Equal(rejected, !Eval(new GlobRule(pattern), path).Allowed);

    [Fact]
    public void Size_rule_applies_to_files_only()
    {
        Assert.Equal(FilterReason.Size, Eval(new SizeRule(100), "a", File(101)).Reason);
        Assert.True(Eval(new SizeRule(100), "a", File(100)).Allowed);
        Assert.True(Eval(new SizeRule(100), "a", NodeState.Directory(T0)).Allowed);
    }

    [Fact]
    public void Path_length_rule()
    {
        Assert.Equal(FilterReason.PathLength, Eval(new PathLengthRule(5), "abc/def").Reason);
        Assert.True(Eval(new PathLengthRule(7), "abc/def").Allowed);
    }

    [Fact]
    public void Invalid_char_rule() =>
        Assert.Equal(FilterReason.InvalidCharacter, Eval(new InvalidCharRule("#"), "a/b#c").Reason);

    [Theory]
    [InlineData("Projekte/alt", true)]
    [InlineData("Projekte/alt/x/y.txt", true)]
    [InlineData("Projekte/alternativ", false)]
    [InlineData("Projekte", false)]
    public void Selective_sync_excludes_the_subtree(string path, bool rejected) =>
        Assert.Equal(rejected, !Eval(new SelectiveSyncRule([RelativePath.Parse("Projekte/alt")]), path).Allowed);

    [Fact]
    public void First_rejecting_rule_wins()
    {
        var engine = new FilterEngine([new SizeRule(1000), new ExtensionRule(["tmp"]), new NameRule(["a.tmp"])]);
        Assert.Equal(FilterReason.Extension, engine.Evaluate(RelativePath.Parse("a.tmp"), File()).Reason);
    }
}

public class ConflictNamerTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 19, 12, 34, 56, TimeSpan.Zero);

    private static string Name(string path, string device = "Laptop", Func<RelativePath, bool>? exists = null) =>
        ConflictNamer.Create(RelativePath.Parse(path), device, When, exists ?? (_ => false)).Value;

    [Fact]
    public void Inserts_the_marker_before_the_extension() =>
        Assert.Equal("a/Bericht (Konflikt Laptop 2026-10-19 1234).docx", Name("a/Bericht.docx"));

    [Fact]
    public void Handles_names_without_extension() =>
        Assert.Equal("Makefile (Konflikt Laptop 2026-10-19 1234)", Name("Makefile"));

    [Fact]
    public void Handles_dot_files() =>
        Assert.Equal(".env (Konflikt Laptop 2026-10-19 1234)", Name(".env"));

    [Fact]
    public void Only_the_last_dot_starts_the_extension() =>
        Assert.Equal("a.tar (Konflikt Laptop 2026-10-19 1234).gz", Name("a.tar.gz"));

    [Fact]
    public void Appends_a_counter_on_collision()
    {
        var taken = new HashSet<string>
        {
            "x (Konflikt Laptop 2026-10-19 1234).txt",
            "x (Konflikt Laptop 2026-10-19 1234 2).txt",
        };
        Assert.Equal(
            "x (Konflikt Laptop 2026-10-19 1234 3).txt",
            Name("x.txt", exists: p => taken.Contains(p.Value)));
    }

    [Theory]
    [InlineData("Lap/top:1", "Laptop1")]
    [InlineData("///", "Gerät")]
    public void Sanitizes_the_device_name(string device, string expected) =>
        Assert.Contains($"(Konflikt {expected} ", Name("x.txt", device));

    [Fact]
    public void Result_is_always_a_valid_path() =>
        Assert.True(RelativePath.TryParse(Name("Ordner/Datei.txt", "PC: <1>"), out _));

    [Fact]
    public void Root_has_no_conflict_copy() =>
        Assert.Throws<ArgumentException>(() => ConflictNamer.Create(RelativePath.Root, "d", When, _ => false));
}
