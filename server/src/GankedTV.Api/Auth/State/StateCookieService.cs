using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.State;

public sealed class StateCookieService : IStateCookieService
{
    public const string CookieName = "gtv_oauth_state";
    public static readonly TimeSpan CookieTtl = TimeSpan.FromMinutes(5);

    private readonly byte[] _secret;

    public StateCookieService(IOptions<OAuthOptions> options)
    {
        var secret = options.Value.StateSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("OAUTH_STATE_SECRET must be configured.");
        }
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    public string IssueState(string? returnTo)
    {
        Span<byte> nonceBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(nonceBytes);
        var nonce = Base64UrlEncode(nonceBytes);

        var returnToBytes = Encoding.UTF8.GetBytes(returnTo ?? "");
        var returnToPart = Base64UrlEncode(returnToBytes);

        var hmac = ComputeHmac(nonce, returnToPart);
        return $"{nonce}.{returnToPart}.{hmac}";
    }

    public StateValidationResult ValidateState(string stateParam, string? cookieValue)
    {
        if (string.IsNullOrEmpty(stateParam) || string.IsNullOrEmpty(cookieValue))
        {
            return StateValidationResult.Invalid;
        }

        if (!FixedTimeEquals(stateParam, cookieValue))
        {
            return StateValidationResult.Invalid;
        }

        var segments = stateParam.Split('.');
        if (segments.Length != 3)
        {
            return StateValidationResult.Invalid;
        }

        var expected = ComputeHmac(segments[0], segments[1]);
        if (!FixedTimeEquals(expected, segments[2]))
        {
            return StateValidationResult.Invalid;
        }

        string? returnTo;
        try
        {
            var returnToBytes = Base64UrlDecode(segments[1]);
            returnTo = returnToBytes.Length == 0 ? null : Encoding.UTF8.GetString(returnToBytes);
        }
        catch (FormatException)
        {
            return StateValidationResult.Invalid;
        }

        return StateValidationResult.Valid(returnTo);
    }

    private string ComputeHmac(string nonce, string returnToPart)
    {
        using var hmac = new HMACSHA256(_secret);
        var payload = Encoding.UTF8.GetBytes($"{nonce}|{returnToPart}");
        var sig = hmac.ComputeHash(payload);
        return Base64UrlEncode(sig);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
