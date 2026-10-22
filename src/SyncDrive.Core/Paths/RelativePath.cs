using System.Text;

namespace SyncDrive.Core.Paths;

/// <summary>
/// Normalisierter, validierter Pfad relativ zur Sync-Wurzel. Trenner ist immer '/'.
/// Jeder Pfad aus Server oder Dateisystem geht durch diesen Typ, bevor er weiterverwendet wird.
/// </summary>
public readonly record struct RelativePath
{
    public const int MaxSegmentLength = 255;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly System.Buffers.SearchValues<char> ForbiddenChars =
        System.Buffers.SearchValues.Create(['<', '>', ':', '"', '\\', '|', '?', '*']);

    private readonly string? _value;

    private RelativePath(string value) => _value = value;

    /// <summary>Der Wurzelpfad (leer).</summary>
    public static RelativePath Root => default;

    public string Value => _value ?? string.Empty;

    public bool IsRoot => Value.Length == 0;

    public string Name => IsRoot ? string.Empty : Value[(Value.LastIndexOf('/') + 1)..];

    public RelativePath Parent
    {
        get
        {
            var i = Value.LastIndexOf('/');
            return i < 0 ? Root : new RelativePath(Value[..i]);
        }
    }

    public IEnumerable<string> Segments => IsRoot ? [] : Value.Split('/');

    public static bool TryParse(string? input, out RelativePath path)
    {
        path = default;
        if (input is null)
        {
            return false;
        }

        var normalized = input.Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0)
        {
            return true; // Wurzel
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (!IsValidSegment(segment))
            {
                return false;
            }
        }

        path = new RelativePath(normalized);
        return true;
    }

    public static RelativePath Parse(string input) =>
        TryParse(input, out var path)
            ? path
            : throw new FormatException($"Ungültiger relativer Pfad: '{input}'");

    public RelativePath Combine(string segment)
    {
        if (!IsValidSegment(segment.Normalize(NormalizationForm.FormC)))
        {
            throw new FormatException($"Ungültiges Pfadsegment: '{segment}'");
        }

        var normalized = segment.Normalize(NormalizationForm.FormC);
        return IsRoot ? new RelativePath(normalized) : new RelativePath(Value + "/" + normalized);
    }

    /// <summary>True, wenn dieser Pfad gleich <paramref name="ancestor"/> ist oder darunter liegt (Groß-/Kleinschreibung ignoriert).</summary>
    public bool IsSameOrDescendantOf(RelativePath ancestor)
    {
        if (ancestor.IsRoot)
        {
            return true;
        }

        if (!Value.StartsWith(ancestor.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Value.Length == ancestor.Value.Length || Value[ancestor.Value.Length] == '/';
    }

    public bool EqualsIgnoreCase(RelativePath other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Value;

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length is 0 or > MaxSegmentLength || segment is "." or ".."
            || segment.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        if (segment[^1] is '.' or ' ')
        {
            return false; // unter Windows nicht darstellbar
        }

        if (segment.AsSpan().IndexOfAny(ForbiddenChars) >= 0)
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];
        return !ReservedNames.Contains(stem);
    }
}
