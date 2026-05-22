using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace GankedTV.Api.Endpoints;

// Shared keyset-cursor helper used by every cursor-paginated list that orders by
// (CreatedAt desc, Guid desc). Base64Url so the raw token survives a query string
// without client-side escaping — DateTimeOffset.ToString("O") includes `+` and `:`
// which URL decoders mangle.
internal static class FeedCursor
{
    private const char Separator = '_';

    public static string Build(DateTimeOffset createdAt, Guid id)
    {
        var payload = $"{createdAt.ToString("O", CultureInfo.InvariantCulture)}{Separator}{id:D}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

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
        catch (FormatException)
        {
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
