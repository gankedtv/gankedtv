using GankedTV.Api.Contracts.Clips;
using Microsoft.Extensions.Caching.Hybrid;

namespace GankedTV.Api.Services.Caching;

/// <summary>
/// Anonymous projection of a feed page held in the cache: the per-caller <c>LikedByMe</c>
/// flag is deliberately left <c>false</c> here and re-stamped per request after a cache hit,
/// so one user's likes never leak to another via the shared cache.
/// </summary>
public sealed record CachedFeedPage(IReadOnlyList<ClipFeedItem> Items, string? NextCursor);

/// <summary>
/// Hot read-path cache for feeds + trending. The interface lets write paths depend on (and
/// tests verify) cache behaviour without binding to HybridCache directly.
/// </summary>
public interface IFeedCache
{
    /// <summary>Cache a feed page (latest or per-game) under the shared feed tag.</summary>
    ValueTask<CachedFeedPage> GetOrCreateFeedAsync(
        string key, Func<CancellationToken, ValueTask<CachedFeedPage>> factory, CancellationToken ct);

    /// <summary>Cache a trending page (TTL-governed; not invalidated on writes).</summary>
    ValueTask<CachedFeedPage> GetOrCreateTrendingAsync(
        string key, Func<CancellationToken, ValueTask<CachedFeedPage>> factory, CancellationToken ct);

    /// <summary>Drop every cached feed page (called best-effort on clip mutations).</summary>
    ValueTask InvalidateFeedsAsync(CancellationToken ct);
}

/// <summary>
/// Thin wrapper over <see cref="HybridCache"/> for the hot read paths (latest feed, per-game
/// feed, trending). HybridCache is L1 in-memory + optional L2 Redis (wired in
/// <see cref="RedisRegistration"/>): with Redis it's shared cluster-wide; without it the API
/// still works off L1 alone. Centralises the short TTL + tag conventions so feed and trending
/// stay DRY and invalidation has a single source of truth.
/// </summary>
public sealed class FeedCache(HybridCache cache) : IFeedCache
{
    /// <summary>Tag on every feed page (latest + per-game). One invalidation clears them all.</summary>
    public const string FeedTag = "clips-feed";

    /// <summary>Tag on trending pages. Self-heals via TTL (ranking is time-windowed/approximate),
    /// so it's not invalidated on writes — only the short expiration governs freshness.</summary>
    public const string TrendingTag = "trending";

    // Short TTL with write-time invalidation. L1 (LocalCacheExpiration) is intentionally
    // shorter than L2 (Expiration): with no cross-pod L1 backplane, RemoveByTagAsync clears
    // L2 immediately but a sibling pod's L1 can serve stale until its local copy lapses —
    // bounded to LocalCacheExpiration. Acceptable for a 30-60s hot-feed cache.
    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromSeconds(60),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    private static readonly string[] FeedTags = [FeedTag];
    private static readonly string[] TrendingTags = [TrendingTag];

    /// <summary>Cache a feed page (latest or per-game) under the <see cref="FeedTag"/>.</summary>
    public ValueTask<CachedFeedPage> GetOrCreateFeedAsync(
        string key,
        Func<CancellationToken, ValueTask<CachedFeedPage>> factory,
        CancellationToken ct) =>
        cache.GetOrCreateAsync(key, factory, Entry, FeedTags, ct);

    /// <summary>Cache a trending page under the <see cref="TrendingTag"/> (TTL-governed).</summary>
    public ValueTask<CachedFeedPage> GetOrCreateTrendingAsync(
        string key,
        Func<CancellationToken, ValueTask<CachedFeedPage>> factory,
        CancellationToken ct) =>
        cache.GetOrCreateAsync(key, factory, Entry, TrendingTags, ct);

    /// <summary>
    /// Drop every cached feed page. Called best-effort on clip create/complete/delete and on
    /// visibility/game changes — any of which can alter the global latest page, so we clear the
    /// whole <see cref="FeedTag"/> rather than reason about which specific pages changed.
    /// <para>
    /// Deliberately NOT called on like/unlike: a cached card's <c>LikeCount</c> is therefore
    /// TTL-bounded-stale (≤ the entry's expiration), the same accepted tradeoff as trending.
    /// Likes are high-frequency, so invalidating on each would defeat the cache; the clip detail
    /// view and like button reflect the true count immediately.
    /// </para>
    /// </summary>
    public ValueTask InvalidateFeedsAsync(CancellationToken ct) =>
        cache.RemoveByTagAsync(FeedTag, ct);
}
