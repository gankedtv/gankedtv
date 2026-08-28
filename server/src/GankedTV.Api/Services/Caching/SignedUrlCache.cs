using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Caching.Hybrid;

namespace GankedTV.Api.Services.Caching;

/// <summary>
/// Hands out a <em>stable</em> presigned GET URL. SigV4 stamps the signing time into the query
/// string, so presigning the same key twice gives two different URLs — and the browser's cache
/// key is the whole URL, so an un-memoised thumbnail is re-fetched on every page load.
/// </summary>
public interface ISignedUrlCache
{
    /// <summary>
    /// Reused across callers until the memo lapses. <paramref name="version"/> is part of the
    /// memo key: bump it when the object changes so viewers get a fresh URL immediately.
    /// </summary>
    ValueTask<string> GetOrCreateAsync(string bucket, string key, int version, CancellationToken ct);
}

/// <inheritdoc cref="ISignedUrlCache"/>
public sealed class SignedUrlCache(HybridCache cache, IObjectStorageService storage) : ISignedUrlCache
{
    // Left at the hour thumbnails have always used: a longer signature widens the window in which
    // a leaked poster URL for a private clip still resolves, and the memo already covers a session.
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(45);

    // `private`, not `public`: the thumbnails bucket is deliberately not anonymous-read, so
    // licensing shared caches to store a private clip's poster buys nothing the browser cache
    // doesn't already give. Bounded by what is left of the signature at the end of the memo
    // window (60 − 45), so a URL handed out on the last tick can't outlive the point it resolves.
    public const string CacheControlHeader = "private, max-age=900";

    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = MemoLifetime,
        LocalCacheExpiration = MemoLifetime,
    };

    public ValueTask<string> GetOrCreateAsync(
        string bucket,
        string key,
        int version,
        CancellationToken ct) =>
        cache.GetOrCreateAsync(
            $"signedurl:{bucket}:{key}:{version}",
            (storage, bucket, key),
            static (state, _) => ValueTask.FromResult(
                state.storage.GetPresignedGetUrl(state.bucket, state.key, UrlLifetime)),
            Entry,
            tags: null,
            ct);
}
