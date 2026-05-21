using GankedTV.Api.Services.ObjectStorage;

namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// Conventions for mirrored game cover objects. Shared by the IGDB import command and the
/// dev seed so both write to the same key and produce the same stable public <c>cover_url</c>.
/// </summary>
public static class GameCovers
{
    public const string ContentType = "image/jpeg";

    /// <summary>
    /// Object key inside <see cref="S3Options.GameCoversBucket"/>. Keyed by slug (unique, stable,
    /// and known before a row has an igdb_id) so the dev seed and the IGDB import write the same
    /// object — a real import overwrites a seeded placeholder in place.
    /// </summary>
    public static string BuildCoverKey(string slug) => $"{slug}.jpg";

    /// <summary>
    /// Stable public URL stored verbatim in <c>cover_url</c>. The covers bucket is
    /// anonymous-read, so this needs no signing/expiry. Prefers the host-visible
    /// <see cref="S3Options.PublicUrl"/>, falling back to the API endpoint for dev.
    /// </summary>
    public static string BuildCoverUrl(S3Options s3, string key)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(s3.PublicUrl) ? s3.Endpoint : s3.PublicUrl)
            .TrimEnd('/');
        return $"{baseUrl}/{s3.GameCoversBucket}/{key}";
    }
}
