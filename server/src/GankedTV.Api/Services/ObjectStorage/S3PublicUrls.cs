namespace GankedTV.Api.Services.ObjectStorage;

/// <summary>
/// Builds stable public (non-presigned) object URLs for the anonymous-read buckets
/// (game-covers, hls). Prefers the host-visible <see cref="S3Options.PublicUrl"/>, falling
/// back to the API endpoint for dev. Path-style to match MinIO (ForcePathStyle).
/// </summary>
public static class S3PublicUrls
{
    public static string BuildUrl(S3Options s3, string bucket, string key)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(s3.PublicUrl) ? s3.Endpoint : s3.PublicUrl)
            .TrimEnd('/');
        return $"{baseUrl}/{bucket}/{key}";
    }
}
