using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace GankedTV.Api.Pagination;

/// <summary>
/// Encodes/decodes a composite <c>(CreatedAt, Id)</c> keyset cursor as an opaque, URL-safe
/// token. The <c>(CreatedAt, Id)</c> pair (rather than <c>CreatedAt</c> alone) keeps paging
/// deterministic when two rows share the same <c>created_at</c> — bulk imports, seed scripts,
/// or same-microsecond inserts would otherwise skip the second row.
/// </summary>
public static class KeysetCursor
{
    private const char Separator = '_';

    /// <summary>
    /// Builds a Base64Url-encoded cursor token. Base64Url keeps the token safe to drop into a
    /// query string without client-side escaping: <c>DateTimeOffset.ToString("O")</c> contains
    /// <c>+</c> (which decoders turn into a space) and <c>:</c>, so encoding keeps it opaque.
    /// </summary>
    public static string Build(DateTimeOffset createdAt, Guid id)
    {
        var payload = $"{createdAt.ToString("O", CultureInfo.InvariantCulture)}{Separator}{id:D}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Parses a cursor token. Returns <c>false</c> for null/empty/corrupt input so callers can
    /// silently fall back to "no cursor" rather than 400-ing on a malformed query string.
    /// </summary>
    public static bool TryParse(string? raw, out DateTimeOffset createdAt, out Guid id)
    {
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
            // FormatException covers invalid Base64Url; ArgumentException covers overflow paths
            // some runtimes can surface. Either way, treat a corrupt cursor as "no cursor".
            return false;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var sep = decoded.IndexOf(Separator);
        if (sep <= 0 || sep == decoded.Length - 1) return false;

        return DateTimeOffset.TryParse(
                decoded[..sep], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt)
            && Guid.TryParse(decoded[(sep + 1)..], out id);
    }
}
