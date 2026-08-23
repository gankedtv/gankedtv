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
    /// How long a handed-out URL stays valid — deliberately left at the one hour thumbnails have
    /// always used. A longer signature would widen the window in which a leaked poster URL for a
    /// private or hidden clip still resolves, and buys very little: the memo below already turns
    /// a browsing session's repeat page loads into cache hits, which is the whole problem.
    /// </summary>
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// <c>Cache-Control</c> stored on the object. Bounded by what is left of the signature at the
    /// end of the memo window (60 − 45 = 15 minutes): a URL handed out on the memo's last tick
    /// must not stay cached past the moment it stops resolving.
    /// </summary>
    public const string CacheControlHeader = "public, max-age=900";

    /// <summary>
    /// How long one URL keeps being handed out. L1 and L2 share it: the entries are one short
    /// string per clip, and a lapsed one just re-signs (a new URL, costing one refetch).
    /// </summary>
    public static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(45);

    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = MemoLifetime,
        LocalCacheExpiration = MemoLifetime,
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
