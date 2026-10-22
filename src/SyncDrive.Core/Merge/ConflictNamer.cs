using System.Globalization;
using SyncDrive.Core.Paths;

namespace SyncDrive.Core.Merge;

/// <summary>
/// Erzeugt den Namen einer Konfliktkopie: <c>name (Konflikt gerät yyyy-MM-dd HHmm).ext</c>.
/// Deterministisch: Uhrzeit, Gerätename und Kollisionsprüfung werden übergeben.
/// </summary>
public static class ConflictNamer
{
    public static RelativePath Create(
        RelativePath original,
        string deviceName,
        DateTimeOffset timestamp,
        Func<RelativePath, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (original.IsRoot)
        {
            throw new ArgumentException("Die Wurzel kann keine Konfliktkopie haben.", nameof(original));
        }

        var (stem, extension) = SplitName(original.Name);
        var stamp = timestamp.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        var device = SanitizeDevice(deviceName);
        var parent = original.Parent;

        for (var counter = 1; ; counter++)
        {
            var suffix = counter == 1 ? string.Empty : $" {counter}";
            var candidate = parent.Combine($"{stem} (Konflikt {device} {stamp}{suffix}){extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static (string Stem, string Extension) SplitName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? (name, string.Empty) : (name[..dot], name[dot..]);
    }

    private static string SanitizeDevice(string deviceName)
    {
        var cleaned = new string(deviceName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length == 0 ? "Gerät" : cleaned;
    }
}
