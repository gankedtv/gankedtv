using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace GankedTV.Api.Auth.Passwords;

// Password rules and tuning live here so the hasher is the single source of
// truth. OWASP Password Storage Cheat Sheet (2024) recommends Argon2id with
// m=19456 KiB, t=2, p=1 as a sane minimum — that's what we use.
//
// Encoding: PHC string format
//   $argon2id$v=19$m=19456,t=2,p=1$<salt_b64>$<hash_b64>
// Storing the parameters alongside the hash lets us rotate cost factors later
// without breaking existing rows: Verify reads m/t/p from the stored string.
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const string AlgorithmName = "argon2id";
    private const int Version = 19;
    private const int DefaultMemoryKb = 19_456;
    private const int DefaultIterations = 2;
    private const int DefaultParallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Algorithm => AlgorithmName;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = ComputeHash(password, salt, DefaultMemoryKb, DefaultIterations, DefaultParallelism);
        return Encode(salt, hash, DefaultMemoryKb, DefaultIterations, DefaultParallelism);
    }

    public bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        if (!TryDecode(encoded, out var parsed))
        {
            return false;
        }

        var computed = ComputeHash(password, parsed.Salt, parsed.MemoryKb, parsed.Iterations, parsed.Parallelism);
        return CryptographicOperations.FixedTimeEquals(computed, parsed.Hash);
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            Iterations = iterations,
            MemorySize = memoryKb,
        };
        return argon.GetBytes(HashBytes);
    }

    private static string Encode(byte[] salt, byte[] hash, int memoryKb, int iterations, int parallelism)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"$argon2id$v={Version}$m={memoryKb},t={iterations},p={parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    private readonly record struct ParsedHash(byte[] Salt, byte[] Hash, int MemoryKb, int Iterations, int Parallelism);

    private static bool TryDecode(string encoded, out ParsedHash parsed)
    {
        parsed = default;
        var parts = encoded.Split('$');
        // Expected: ["", "argon2id", "v=19", "m=19456,t=2,p=1", "<salt>", "<hash>"]
        if (parts.Length != 6 || parts[1] != AlgorithmName)
        {
            return false;
        }

        if (!parts[2].StartsWith("v=", StringComparison.Ordinal)
            || !int.TryParse(parts[2].AsSpan(2), out var version)
            || version != Version)
        {
            return false;
        }

        if (!TryParseParams(parts[3], out var memoryKb, out var iterations, out var parallelism))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[4]);
            var hash = Convert.FromBase64String(parts[5]);
            parsed = new ParsedHash(salt, hash, memoryKb, iterations, parallelism);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseParams(string raw, out int memoryKb, out int iterations, out int parallelism)
    {
        memoryKb = iterations = parallelism = 0;
        foreach (var part in raw.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }
            var key = part[..eq];
            var value = part.AsSpan(eq + 1);
            if (!int.TryParse(value, out var n))
            {
                return false;
            }
            switch (key)
            {
                case "m": memoryKb = n; break;
                case "t": iterations = n; break;
                case "p": parallelism = n; break;
                default: return false;
            }
        }
        return memoryKb > 0 && iterations > 0 && parallelism > 0;
    }
}
