namespace GankedTV.Api.Services.ObjectStorage;

public sealed class S3Options
{
    public string Endpoint { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string? PublicUrl { get; set; }
    public string ClipsBucket { get; set; } = "clips";
    public string ThumbnailsBucket { get; set; } = "thumbnails";

    // Game cover art mirrored from IGDB. Unlike clips/thumbnails (private, served via
    // presigned URLs) this bucket is made anonymous-read so cover_url can hold a stable,
    // CDN-cacheable public URL — see S3ObjectStorageService.EnsureBucketsAsync.
    public string GameCoversBucket { get; set; } = "game-covers";
}
