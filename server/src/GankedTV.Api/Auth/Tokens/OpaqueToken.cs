using System.Security.Cryptography;
using System.Text;

namespace GankedTV.Api.Auth.Tokens;

// Shared generation + hashing for high-entropy opaque secrets (refresh tokens, API keys).
// The raw value is 256 bits of CSPRNG output, so a plain SHA-256 lookup hash is both safe
// (not brute-forceable) and indexable (unlike argon2, which we can't afford per request).
public static class OpaqueToken
{
    private const int TokenBytes = 32;

    // base64url without padding: URL-safe, header-safe, and no '=' to trip up copy/paste.
    public static string Generate(string prefix = "")
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
