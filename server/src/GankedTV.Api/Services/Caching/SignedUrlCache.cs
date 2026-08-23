using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Caching.Hybrid;

namespace GankedTV.Api.Services.Caching;

/// <summary>
/// Hands out a <em>stable</em> presigned GET URL for an object.
///
/// SigV4 stamps <c>X-Amz-Date</c> and a signature derived from it, so presigning the same key
/// twice a second apart produces two different URLs. The browser's cache key is the full URL,
/// which means an un-memoised presigned thumbnail is re-downloaded on every single page load —
/// no disk cache, no CDN hit, nothing. Memoising the URL for most of its validity window turns
/// those repeat loads into cache hits without making the bucket public.
/// </summary>
public interface ISignedUrlCache
{
    /// <summary>
    /// The presigned GET URL for <paramref name="key"/>, reused across callers until the memo
    /// lapses. <paramref name="version"/> participates in the memo key: bump it when the object
    /// behind the key changes so viewers get a fresh URL — and therefore a cache miss —
    /// immediately rather than waiting out the TTL.
    /// </summary>
    ValueTask<string> GetOrCreateAsync(string bucket, string key, int version, CancellationToken ct);
}

/// <inheritdoc cref="ISignedUrlCache"/>
public sealed class SignedUrlCache(HybridCache cache, IObjectStorageService storage) : ISignedUrlCache
{
    public const string Tag = "signed-urls";

    /// <summary>
    /// How long a handed-out URL stays valid. Seven days is SigV4's ceiling for a presigned URL.
    /// </summary>
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Matching <c>Cache-Control</c> for the stored object. Aligned with the L2 memo so a browser
    /// never holds a cached copy of a URL the memo has already stopped handing out.
    /// </summary>
    public const string CacheControlHeader = "public, max-age=518400";

    // L2 (Redis) is the real window: six days of a seven-day signature, so a URL handed out at
    // the last moment still has a day of validity left. L1 is deliberately shorter — it is a
    // per-pod dictionary that would otherwise hold one string per clip for the better part of a
    // week, and a lapsed L1 entry just re-signs (the URL changes, costing one refetch).
    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromDays(6),
        LocalCacheExpiration = TimeSpan.FromHours(12),
    };

    private static readonly string[] Tags = [Tag];

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
            Tags,
            ct);
}
