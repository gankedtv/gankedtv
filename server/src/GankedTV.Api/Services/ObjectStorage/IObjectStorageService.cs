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

    Task DeleteObjectAsync(string bucket, string key, CancellationToken ct = default);

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
}
