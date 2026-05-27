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

    // Transient, just-in-time H.264 HLS renditions (master.m3u8 + variant playlists + segments)
    // produced on demand for devices that can't decode a clip's stored master. Anonymous-read
    // like game-covers so the master playlist is a stable public URL and relative segment URIs
    // resolve without per-segment presigning. Auto-evicted by a lifecycle rule after
    // StreamCacheTtlDays — these renditions are a cache, not a system of record.
    public string StreamCacheBucket { get; set; } = "stream-cache";

    // Days a cached rendition survives before the bucket's lifecycle expiry removes it.
    public int StreamCacheTtlDays { get; set; } = 14;

    // User avatars and banners. Anonymous-read like game-covers so the stored URL is a stable,
    // cacheable public URL that doesn't need presigning on every read. Object keys carry a
    // ULID suffix so a replacement upload writes to a new key, the DB swap is atomic, and the
    // old object can be deleted afterwards without browser-cache invalidation pain.
    public string AvatarsBucket { get; set; } = "avatars";
}
