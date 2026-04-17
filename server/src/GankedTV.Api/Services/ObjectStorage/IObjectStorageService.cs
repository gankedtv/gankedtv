namespace GankedTV.Api.Services.ObjectStorage;

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
}
