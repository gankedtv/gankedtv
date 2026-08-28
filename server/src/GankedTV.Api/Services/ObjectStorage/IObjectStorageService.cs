namespace GankedTV.Api.Services.ObjectStorage;

public sealed record ObjectMetadata(long SizeBytes, string? ContentType);

public interface IObjectStorageService
{
    Task EnsureBucketsAsync(CancellationToken ct = default);

    string GetPresignedPutUrl(
        string bucket,
        string key,
        string contentType,
        TimeSpan? expiry = null);

    string GetPresignedGetUrl(
        string bucket,
        string key,
        TimeSpan? expiry = null);

    // GET URL for a server-side media worker to fetch source bytes with. Signed against (and
    // pointing at) the internal endpoint — S3Options.InternalEndpoint when set, else the internal
    // Endpoint — never the browser-facing PublicUrl. Keeps ffmpeg/ffprobe on a split worker host
    // off a public certificate they can't verify (and off hairpin routing to the public host).
    string GetPresignedGetUrlForWorker(
        string bucket,
        string key,
        TimeSpan? expiry = null);

    Task DeleteObjectAsync(string bucket, string key, CancellationToken ct = default);

    // Deletes every object under a key prefix (e.g. a clip's cached HLS rendition under
    // "{clipId}/..."). Used so deleting a clip doesn't leave its publicly-readable
    // stream-cache objects lingering until the lifecycle TTL.
    Task DeleteByPrefixAsync(string bucket, string prefix, CancellationToken ct = default);

    Task<ObjectMetadata?> GetObjectMetadataAsync(
        string bucket,
        string key,
        CancellationToken ct = default);

    // Direct PUT for server-generated artifacts (thumbnails, future transcodes). Browser
    // uploads still use GetPresignedPutUrl — this path is for the API process / worker
    // writing bytes it produced itself.
    Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken ct = default);

    // As above, plus a `Cache-Control` stored on the object and returned on every GET, including
    // presigned ones. A separate overload so `ct` stays last on both (CA1068).
    Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        string? cacheControl,
        CancellationToken ct = default);
}
