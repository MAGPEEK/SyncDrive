using System.Text.RegularExpressions;

namespace SyncDrive.Architecture.Tests;

/// <summary>
/// Farben kommen aus den Tokens des Design-Systems „SyncDrive“: in der Weboberfläche nur als CSS-Variablen aus <c>:root</c>, im
/// Desktop-Client nur als Ressourcen aus <c>App.axaml</c>. Ein fester Farbwert anderswo umginge das Design-System (und Hell/Dunkel).
/// </summary>
public sealed partial class DesignTokenTests
{
    private static readonly string Src = Path.Combine(FindUp("Directory.Build.props"), "src");

    [Fact]
    public void Web_styles_use_colours_only_from_the_token_block()
    {
        var css = File.ReadAllText(Path.Combine(Src, "SyncDrive.Web", "wwwroot", "css", "app.css"));
        var afterTokens = css[(css.IndexOf("font-family:", StringComparison.Ordinal) + 1)..];

        Assert.Empty(HexColour().Matches(afterTokens).Select(m => m.Value));
    }

    [Fact]
    public void The_dark_theme_only_overrides_tokens_the_light_theme_defines()
    {
        var css = File.ReadAllText(Path.Combine(Src, "SyncDrive.Web", "wwwroot", "css", "app.css"));
        var dark = css.IndexOf(":root[data-theme=\"dark\"]", StringComparison.Ordinal);
        var light = TokenName().Matches(css[..dark]).Select(m => m.Value).ToHashSet();
        var darkBlock = css[dark..css.IndexOf('}', dark)];

        Assert.DoesNotContain(TokenName().Matches(darkBlock).Select(m => m.Value), t => !light.Contains(t));
        Assert.Contains("--bg:", darkBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_components_use_no_fixed_colours_except_the_logo()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Src, "SyncDrive.Web"), "*.razor", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "Logo.razor") // das Zeichen wird nie umgefärbt
            .SelectMany(f => HexColour().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Desktop_windows_use_colours_only_from_the_app_resources()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Src, "SyncDrive.Client.Tray"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "App.axaml")
            .SelectMany(f => FixedColourAttribute().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}"));
        var appStyles = File.ReadAllText(Path.Combine(Src, "SyncDrive.Client.Tray", "App.axaml"));
        var styles = appStyles[appStyles.IndexOf("<Application.Styles>", StringComparison.Ordinal)..];

        Assert.Empty(offenders.Concat(FixedColourAttribute().Matches(styles).Select(m => $"App.axaml (Styles): {m.Value}")));
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

    [GeneratedRegex(@"--[a-z0-9-]+(?=:)")]
    private static partial Regex TokenName();

    [GeneratedRegex(@"#[0-9A-Fa-f]{6}\b|#[0-9A-Fa-f]{3}\b(?![0-9A-Fa-f])")]
    private static partial Regex HexColour();

    /// <summary>Attributwerte wie <c>"#2F78D0"</c>, <c>"White"</c> oder <c>"Black"</c>.</summary>
    [GeneratedRegex(@"=""(#[0-9A-Fa-f]{6,8}|White|Black)""")]
    private static partial Regex FixedColourAttribute();
}
