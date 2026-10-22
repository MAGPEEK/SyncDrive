using System.Text.RegularExpressions;
using SyncDrive.Core.Model;
using SyncDrive.Core.Paths;

namespace SyncDrive.Core.Filtering;

public enum FilterReason
{
    Extension,
    Name,
    Glob,
    Size,
    PathLength,
    InvalidCharacter,
    SelectiveSync,
    Prefix,
}

public readonly record struct FilterDecision(bool Allowed, FilterReason? Reason)
{
    public static FilterDecision Allow { get; } = new(true, null);

    public static FilterDecision Reject(FilterReason reason) => new(false, reason);
}

public interface IFilterRule
{
    /// <summary>Liefert den Ablehnungsgrund oder null, wenn die Regel den Pfad zulässt.</summary>
    FilterReason? Check(RelativePath path, NodeState state);
}

/// <summary>Wertet Regeln der Reihe nach aus; die erste ablehnende gewinnt. Reine Funktion.</summary>
public sealed class FilterEngine(IReadOnlyList<IFilterRule> rules)
{
    public FilterDecision Evaluate(RelativePath path, NodeState state)
    {
        foreach (var rule in rules)
        {
            if (rule.Check(path, state) is { } reason)
            {
                return FilterDecision.Reject(reason);
            }
        }

        return FilterDecision.Allow;
    }
}

/// <summary>Lehnt Dateien mit einer der Endungen ab (ohne Punkt, ohne Beachtung der Schreibweise).</summary>
public sealed class ExtensionRule(IEnumerable<string> extensions) : IFilterRule
{
    private readonly HashSet<string> _extensions =
        new(extensions.Select(e => e.TrimStart('.')), StringComparer.OrdinalIgnoreCase);

    public FilterReason? Check(RelativePath path, NodeState state)
    {
        if (state.Kind != NodeKind.File)
        {
            return null;
        }

        var name = path.Name;
        var dot = name.LastIndexOf('.');
        return dot > 0 && _extensions.Contains(name[(dot + 1)..]) ? FilterReason.Extension : null;
    }
}

/// <summary>
/// Lehnt Dateien und Ordner mit genau diesem Namen ab (an jeder Stelle im Baum) samt Inhalt. Mit <paramref name="filesOnly"/> gilt die Regel nur
/// für den Namen einer Datei selbst (z. B. eine Datei namens <c>tmp</c>), nicht für gleichnamige Ordner.
/// </summary>
public sealed class NameRule(IEnumerable<string> names, bool filesOnly = false) : IFilterRule
{
    private readonly HashSet<string> _names = new(names, StringComparer.OrdinalIgnoreCase);

    public FilterReason? Check(RelativePath path, NodeState state)
    {
        if (filesOnly)
        {
            return state.Kind == NodeKind.File && _names.Contains(path.Name) ? FilterReason.Name : null;
        }

        return path.Segments.Any(_names.Contains) ? FilterReason.Name : null;
    }
}

/// <summary>
/// Lehnt Namen ab, die mit einem der Präfixe beginnen (z. B. <c>~</c> für Office-Sperrdateien, <c>.</c> für versteckte Dateien).
/// Mit <paramref name="filesOnly"/> nur Dateien; sonst auch Ordner samt Inhalt.
/// </summary>
public sealed class PrefixRule(IEnumerable<string> prefixes, bool filesOnly = false) : IFilterRule
{
    private readonly string[] _prefixes = [.. prefixes.Where(p => p.Length > 0)];

    public FilterReason? Check(RelativePath path, NodeState state)
    {
        if (filesOnly)
        {
            return state.Kind == NodeKind.File && Matches(path.Name) ? FilterReason.Prefix : null;
        }

        return path.Segments.Any(Matches) ? FilterReason.Prefix : null;
    }

    private bool Matches(string name) => _prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Glob-Muster: <c>*</c> (ohne '/'), <c>**</c> (beliebig tief), <c>?</c>. Enthält das Muster kein '/', wird nur der Name geprüft.
/// </summary>
public sealed class GlobRule : IFilterRule
{
    private readonly Regex _regex;
    private readonly bool _nameOnly;

    public GlobRule(string pattern)
    {
        _nameOnly = !pattern.Contains('/', StringComparison.Ordinal);
        _regex = new Regex(ToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }

    public FilterReason? Check(RelativePath path, NodeState state) =>
        _regex.IsMatch(_nameOnly ? path.Name : path.Value) ? FilterReason.Glob : null;

    private static string ToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            switch (glob[i])
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    sb.Append(".*");
                    i++;
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(glob[i].ToString()));
                    break;
            }
        }

        return sb.Append('$').ToString();
    }
}

/// <summary>Lehnt Dateien ab, die größer als das Limit sind.</summary>
public sealed class SizeRule(long maxBytes) : IFilterRule
{
    public FilterReason? Check(RelativePath path, NodeState state) =>
        state.Kind == NodeKind.File && state.Size > maxBytes ? FilterReason.Size : null;
}

/// <summary>Lehnt Pfade ab, die länger als das Limit sind (Zeichen).</summary>
public sealed class PathLengthRule(int maxLength) : IFilterRule
{
    public FilterReason? Check(RelativePath path, NodeState state) =>
        path.Value.Length > maxLength ? FilterReason.PathLength : null;
}

/// <summary>Lehnt Namen mit unerwünschten Zeichen ab (zusätzlich zur Grundvalidierung in <see cref="RelativePath"/>).</summary>
public sealed class InvalidCharRule(string characters) : IFilterRule
{
    public FilterReason? Check(RelativePath path, NodeState state) =>
        path.Value.AsSpan().IndexOfAny(characters) >= 0 ? FilterReason.InvalidCharacter : null;
}

/// <summary>Selektive Synchronisierung: abgewählte Ordner samt Inhalt.</summary>
public sealed class SelectiveSyncRule(IEnumerable<RelativePath> excluded) : IFilterRule
{
    private readonly RelativePath[] _excluded = excluded.ToArray();

    public FilterReason? Check(RelativePath path, NodeState state) =>
        _excluded.Any(path.IsSameOrDescendantOf) ? FilterReason.SelectiveSync : null;
}
