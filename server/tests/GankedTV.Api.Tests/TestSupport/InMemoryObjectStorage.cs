using GankedTV.Api.Services.ObjectStorage;

namespace GankedTV.Api.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IObjectStorageService"/> for tests that need to assert on stored
/// objects without a real S3 container. Records puts and serves metadata from the map.
/// </summary>
public sealed class InMemoryObjectStorage : IObjectStorageService
{
    public Dictionary<(string Bucket, string Key), byte[]> Objects { get; } = new();
    public List<(string Bucket, string Key, string ContentType, byte[] Bytes)> PutCalls { get; } = new();
    public int EnsureBucketsCallCount { get; private set; }

    public Task EnsureBucketsAsync(CancellationToken ct = default)
    {
        EnsureBucketsCallCount++;
        return Task.CompletedTask;
    }

    public Task<ObjectMetadata?> GetObjectMetadataAsync(string bucket, string key, CancellationToken ct = default) =>
        Task.FromResult(Objects.TryGetValue((bucket, key), out var bytes)
            ? new ObjectMetadata(bytes.Length, null)
            : null);

    public async Task PutObjectAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        Objects[(bucket, key)] = bytes;
        PutCalls.Add((bucket, key, contentType, bytes));
    }

    public Task DeleteObjectAsync(string bucket, string key, CancellationToken ct = default)
    {
        Objects.Remove((bucket, key));
        return Task.CompletedTask;
    }

    public string GetPresignedPutUrl(string bucket, string key, string contentType, TimeSpan? expiry = null) => string.Empty;
    public string GetPresignedGetUrl(string bucket, string key, TimeSpan? expiry = null) => string.Empty;
}
