using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace GankedTV.Api.Pagination;

/// <summary>
/// Keyset cursor for the <c>sort=top</c> feed, encoding the full ranking tuple
/// <c>(LikeCount, ViewCount, CreatedAt, Id)</c> as an opaque, URL-safe token. The latest feed's
/// <see cref="KeysetCursor"/> keys on <c>(CreatedAt, Id)</c> alone — enough for a chronological
/// order — but a likes-ranked feed isn't monotonic in <c>created_at</c>, so the sort key itself
/// has to ride in the cursor for paging to stay stable across pages.
/// </summary>
public static class TopFeedCursor
{
    private const char Separator = '_';

    /// <summary>
    /// Builds a Base64Url-encoded cursor token. Base64Url keeps it query-string safe without
    /// client escaping (<c>DateTimeOffset.ToString("O")</c> contains <c>+</c> and <c>:</c>).
    /// The four fields never contain the <c>_</c> separator: counts are non-negative integers,
    /// the round-trip timestamp uses <c>-</c>/<c>:</c>/<c>.</c>/<c>+</c>, and a <c>D</c> Guid uses <c>-</c>.
    /// </summary>
    public static string Build(int likeCount, int viewCount, DateTimeOffset createdAt, Guid id)
    {
        var payload = string.Join(
            Separator,
            likeCount.ToString(CultureInfo.InvariantCulture),
            viewCount.ToString(CultureInfo.InvariantCulture),
            createdAt.ToString("O", CultureInfo.InvariantCulture),
            id.ToString("D"));
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Parses a cursor token. Returns <c>false</c> for null/empty/corrupt input so callers can
    /// silently fall back to "no cursor" rather than 400-ing on a malformed query string.
    /// </summary>
    public static bool TryParse(
        string? raw, out int likeCount, out int viewCount, out DateTimeOffset createdAt, out Guid id)
    {
        likeCount = 0;
        viewCount = 0;
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

        var parts = Encoding.UTF8.GetString(bytes).Split(Separator);
        if (parts.Length != 4) return false;

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out likeCount)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out viewCount)
            && DateTimeOffset.TryParse(
                parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt)
            && Guid.TryParse(parts[3], out id);
    }
}
