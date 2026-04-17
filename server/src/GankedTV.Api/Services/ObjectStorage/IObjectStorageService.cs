namespace GankedTV.Api.Services.ObjectStorage;

public interface IObjectStorageService
{
    Task EnsureBucketsAsync(CancellationToken ct = default);

    Task<string> GetPresignedPutUrlAsync(
        string bucket,
        string key,
        string contentType,
        TimeSpan? expiry = null,
        CancellationToken ct = default);

    Task<string> GetPresignedGetUrlAsync(
        string bucket,
        string key,
        TimeSpan? expiry = null,
        CancellationToken ct = default);

    Task DeleteObjectAsync(string bucket, string key, CancellationToken ct = default);
}
