using System.Security.Cryptography;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Clips;

public static class ShareCodeGenerator
{
    private const int CodeLength = 8;
    private const int MaxRetries = 5;
    private const string Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Next()
    {
        Span<byte> buf = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(buf);
        return string.Create(CodeLength, buf.ToArray(), static (chars, bytes) =>
        {
            for (var i = 0; i < CodeLength; i++)
                chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        });
    }

    public static async Task<string> GenerateUniqueAsync(
        DbSet<Clip> clips, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var code = Next();
            var taken = await clips.AnyAsync(c => c.ShareCode == code, ct);
            if (!taken) return code;
        }
        throw new InvalidOperationException(
            $"Failed to generate a unique share code after {MaxRetries} attempts.");
    }
}
