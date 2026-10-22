using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using SyncDrive.Server.App.Accounts;

namespace SyncDrive.Infra.Security;

/// <summary>
/// Argon2id mit Salz je Passwort. Das Ergebnis trägt seine Parameter
/// (<c>argon2id$m=…,t=…,p=…$salt$hash</c>), damit sich die Kosten später erhöhen lassen, ohne alte Hashes zu entwerten.
/// </summary>
public sealed class Argon2PasswordHasher(Argon2Options? options = null) : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Algorithm = "argon2id";

    private readonly Argon2Options _options = options ?? new Argon2Options();

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, _options.MemoryKiB, _options.Iterations, _options.Parallelism, HashBytes);
        return $"{Algorithm}$m={_options.MemoryKiB},t={_options.Iterations},p={_options.Parallelism}" +
               $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        if (password.Length == 0)
        {
            return false; // Argon2 lehnt leere Passwörter ab; ein Konto kann keines haben (Mindestlänge)
        }

        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm || !TryParseParameters(parts[1], out var m, out var t, out var p))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Compute(password, salt, m, t, p, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static bool TryParseParameters(string text, out int m, out int t, out int p)
    {
        (m, t, p) = (0, 0, 0);
        var fields = text.Split(',');
        return fields.Length == 3
            && fields[0].StartsWith("m=", StringComparison.Ordinal) && int.TryParse(fields[0].AsSpan(2), out m)
            && fields[1].StartsWith("t=", StringComparison.Ordinal) && int.TryParse(fields[1].AsSpan(2), out t)
            && fields[2].StartsWith("p=", StringComparison.Ordinal) && int.TryParse(fields[2].AsSpan(2), out p)
            && m >= 8 && t >= 1 && p >= 1;
    }
}

/// <summary>Standard nach OWASP-Empfehlung: 64 MiB, 3 Durchläufe, 4 Lanes.</summary>
public sealed record Argon2Options(int MemoryKiB = 65536, int Iterations = 3, int Parallelism = 4);
