using System.Globalization;

namespace SyncDrive.Core.Model;

/// <summary>SHA-256 eines Inhalts, textlich als <c>sha256:&lt;hex&gt;</c>.</summary>
public readonly record struct ContentHash
{
    private const string Prefix = "sha256:";
    private const int HexLength = 64;

    private ContentHash(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? input, out ContentHash hash)
    {
        hash = default;
        if (input is null || !input.StartsWith(Prefix, StringComparison.Ordinal) ||
            input.Length != Prefix.Length + HexLength)
        {
            return false;
        }

        foreach (var c in input.AsSpan(Prefix.Length))
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        hash = new ContentHash(input.ToLower(CultureInfo.InvariantCulture));
        return true;
    }

    public static ContentHash Parse(string input) =>
        TryParse(input, out var hash) ? hash : throw new FormatException($"Ungültiger Hash: '{input}'");

    public static ContentHash FromBytes(ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != HexLength / 2)
        {
            throw new ArgumentException("SHA-256 hat 32 Bytes.", nameof(sha256));
        }

        return new ContentHash(Prefix + Convert.ToHexStringLower(sha256));
    }

    public override string ToString() => Value ?? string.Empty;
}
