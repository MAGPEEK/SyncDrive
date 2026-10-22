using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Core.Tests;

public class RelativePathTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("a/b/c.txt")]
    [InlineData("Ordner mit Leerzeichen/Äpfel.docx")]
    [InlineData(".hidden")]
    public void Accepts_valid_paths(string input) => Assert.True(RelativePath.TryParse(input, out _));

    [Theory]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("../a")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("a\\b")]
    [InlineData("C:/a")]
    [InlineData("a/b?.txt")]
    [InlineData("a/b*")]
    [InlineData("CON")]
    [InlineData("a/nul.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("a\u0000b")]
    public void Rejects_unsafe_paths(string input) => Assert.False(RelativePath.TryParse(input, out _));

    [Fact]
    public void Rejects_null() => Assert.False(RelativePath.TryParse(null, out _));

    [Fact]
    public void Rejects_overlong_segment() =>
        Assert.False(RelativePath.TryParse(new string('x', RelativePath.MaxSegmentLength + 1), out _));

    [Fact]
    public void Empty_string_is_the_root()
    {
        Assert.True(RelativePath.TryParse(string.Empty, out var root));
        Assert.True(root.IsRoot);
        Assert.Equal(RelativePath.Root, root);
    }

    [Fact]
    public void Normalizes_to_nfc()
    {
        var decomposed = "Ä".Normalize(System.Text.NormalizationForm.FormD); // A + U+0308
        var path = RelativePath.Parse(decomposed + ".txt");
        Assert.Equal("Ä.txt", path.Value);
        Assert.Equal(RelativePath.Parse("Ä.txt"), path);
    }

    [Fact]
    public void Name_and_parent()
    {
        var path = RelativePath.Parse("a/b/c.txt");
        Assert.Equal("c.txt", path.Name);
        Assert.Equal(RelativePath.Parse("a/b"), path.Parent);
        Assert.Equal(RelativePath.Root, RelativePath.Parse("a").Parent);
    }

    [Fact]
    public void Combine_validates_the_segment()
    {
        Assert.Equal("a/b", RelativePath.Parse("a").Combine("b").Value);
        Assert.Equal("b", RelativePath.Root.Combine("b").Value);
        Assert.Throws<FormatException>(() => RelativePath.Parse("a").Combine(".."));
        Assert.Throws<FormatException>(() => RelativePath.Parse("a").Combine("x/y"));
    }

    [Theory]
    [InlineData("a/b", "a", true)]
    [InlineData("a", "a", true)]
    [InlineData("A/B", "a", true)]
    [InlineData("ab", "a", false)]
    [InlineData("a", "a/b", false)]
    [InlineData("a", "", true)]
    public void Descendant_check(string path, string ancestor, bool expected) =>
        Assert.Equal(expected, RelativePath.Parse(path).IsSameOrDescendantOf(RelativePath.Parse(ancestor)));
}

public class ContentHashTests
{
    private static readonly string Valid = "sha256:" + new string('a', 64);

    [Fact]
    public void Parses_and_lowercases() =>
        Assert.Equal(Valid, ContentHash.Parse("sha256:" + new string('A', 64)).Value);

    [Theory]
    [InlineData("")]
    [InlineData("sha256:abc")]
    [InlineData("md5:00000000000000000000000000000000")]
    public void Rejects_malformed(string input) => Assert.False(ContentHash.TryParse(input, out _));

    [Fact]
    public void Rejects_non_hex() =>
        Assert.False(ContentHash.TryParse("sha256:" + new string('g', 64), out _));

    [Fact]
    public void FromBytes_formats_hex() =>
        Assert.Equal("sha256:" + new string('0', 64), ContentHash.FromBytes(new byte[32]).Value);

    [Fact]
    public void FromBytes_requires_32_bytes() =>
        Assert.Throws<ArgumentException>(() => ContentHash.FromBytes(new byte[5]));
}
