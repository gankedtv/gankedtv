using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace GankedTV.Api.Pagination;

/// <summary>
/// Extends the opaque keyset token to a <c>(tier, createdAt, id)</c> triple for the tiered
/// For You feed. Payload <c>{tier}_{createdAt:O}_{id:D}</c>, Base64Url-encoded (same scheme as
/// <see cref="KeysetCursor"/>). Neither the <c>O</c> date format nor a <c>D</c>-format Guid
/// contains <c>_</c>, so <c>Split('_', 3)</c> decodes unambiguously.
/// </summary>
public static class TieredKeysetCursor
{
    private const char Separator = '_';

    public static string Build(int tier, DateTimeOffset createdAt, Guid id)
    {
        var payload =
            $"{tier.ToString(CultureInfo.InvariantCulture)}{Separator}" +
            $"{createdAt.ToString("O", CultureInfo.InvariantCulture)}{Separator}{id:D}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Parses a tiered cursor. Returns <c>false</c> on null/empty/corrupt input (including a
    /// plain <see cref="KeysetCursor"/> token, which lacks the leading tier segment), leaving
    /// <paramref name="tier"/> at 0 so callers silently restart from tier 0.
    /// </summary>
    public static bool TryParse(string? raw, out int tier, out DateTimeOffset createdAt, out Guid id)
    {
        tier = 0;
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(raw);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var parts = decoded.Split(Separator, 3);
        if (parts.Length != 3) return false;

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out tier)
            && DateTimeOffset.TryParse(
                parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt)
            && Guid.TryParse(parts[2], out id);
    }
}
