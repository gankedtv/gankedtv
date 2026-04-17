using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Auth;

public static class UsernameGenerator
{
    public const int MaxLength = 24;
    public const int MaxRetries = 5;
    public const string Fallback = "player";

    public static string Slugify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fallback;
        }

        // Normalise to NFD and drop combining marks so "Jürgen" → "Jurgen", "Köhler" → "Kohler".
        var decomposed = raw.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed.ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
            {
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch == '.')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-', '_');
        if (slug.Length == 0)
        {
            return Fallback;
        }

        return slug.Length <= MaxLength ? slug : slug[..MaxLength];
    }

    public static async Task<string> GenerateUniqueAsync(
        string? raw,
        DbSet<User> users,
        CancellationToken ct = default)
    {
        var baseSlug = Slugify(raw);
        if (!await users.AnyAsync(u => u.Username == baseSlug, ct))
        {
            return baseSlug;
        }

        for (var i = 0; i < MaxRetries; i++)
        {
            var candidate = AppendSuffix(baseSlug);
            if (!await users.AnyAsync(u => u.Username == candidate, ct))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not generate a unique username from '{baseSlug}' after {MaxRetries} attempts.");
    }

    private static string AppendSuffix(string baseSlug)
    {
        const int suffixHexChars = 4;
        const int suffixTotal = suffixHexChars + 1; // -XXXX
        var trimTo = Math.Min(baseSlug.Length, MaxLength - suffixTotal);
        var trimmed = trimTo <= 0 ? Fallback : baseSlug[..trimTo];

        Span<byte> buf = stackalloc byte[2];
        RandomNumberGenerator.Fill(buf);
        var hex = Convert.ToHexString(buf).ToLowerInvariant();
        return $"{trimmed}-{hex}";
    }
}
