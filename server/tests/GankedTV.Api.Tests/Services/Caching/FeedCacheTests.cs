using FluentAssertions;
using GankedTV.Api.Services.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GankedTV.Api.Tests.Services.Caching;

// Exercises FeedCache against a real HybridCache (L1-only — no Redis registered, mirroring local
// dev / a Redis outage). Verifies the cache-hit, tag-invalidation, and tag-isolation behaviour the
// endpoints rely on.
public class FeedCacheTests
{
    private static (FeedCache cache, ServiceProvider sp) Build()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        return (new FeedCache(sp.GetRequiredService<HybridCache>()), sp);
    }

    private static CachedFeedPage Page(string? cursor = null) => new([], cursor);

    [Fact]
    public async Task GetOrCreateFeedAsync_SecondCall_ServedFromCache()
    {
        var (cache, sp) = Build();
        await using var _ = sp;
        var calls = 0;

        ValueTask<CachedFeedPage> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<CachedFeedPage>(Page("c1"));
        }

        var first = await cache.GetOrCreateFeedAsync("feed:latest:20", Factory, default);
        var second = await cache.GetOrCreateFeedAsync("feed:latest:20", Factory, default);

        calls.Should().Be(1, "the second call must hit the cache, not the factory");
        first.NextCursor.Should().Be("c1");
        second.NextCursor.Should().Be("c1");
    }

    [Fact]
    public async Task InvalidateFeedsAsync_ForcesFactoryToRunAgain()
    {
        var (cache, sp) = Build();
        await using var _ = sp;
        var calls = 0;
        ValueTask<CachedFeedPage> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<CachedFeedPage>(Page());
        }

        await cache.GetOrCreateFeedAsync("feed:latest:20", Factory, default);
        await cache.InvalidateFeedsAsync(default);
        await cache.GetOrCreateFeedAsync("feed:latest:20", Factory, default);

        calls.Should().Be(2, "invalidation must drop the entry so the next read re-queries");
    }

    [Fact]
    public async Task InvalidateFeedsAsync_DoesNotEvictTrending()
    {
        // Trending lives under its own tag and is TTL-governed; a feed invalidation must leave it.
        var (cache, sp) = Build();
        await using var _ = sp;
        var trendingCalls = 0;
        ValueTask<CachedFeedPage> Trending(CancellationToken _)
        {
            trendingCalls++;
            return new ValueTask<CachedFeedPage>(Page());
        }

        await cache.GetOrCreateTrendingAsync("feed:trending:24h:20", Trending, default);
        await cache.InvalidateFeedsAsync(default);
        await cache.GetOrCreateTrendingAsync("feed:trending:24h:20", Trending, default);

        trendingCalls.Should().Be(1, "the feed tag must not evict trending entries");
    }

    [Fact]
    public async Task GetOrCreateLeaderboardAsync_SecondCall_ServedFromCache()
    {
        var (cache, sp) = Build();
        await using var _ = sp;
        var calls = 0;
        ValueTask<int> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<int>(42);
        }

        var first = await cache.GetOrCreateLeaderboardAsync("lb:global:week:10:10", Factory, default);
        var second = await cache.GetOrCreateLeaderboardAsync("lb:global:week:10:10", Factory, default);

        calls.Should().Be(1, "the second call must hit the cache, not the factory");
        first.Should().Be(42);
        second.Should().Be(42);
    }

    [Fact]
    public async Task InvalidateFeedsAsync_DoesNotEvictLeaderboards()
    {
        // Leaderboards live under their own tag and are TTL-governed; a feed invalidation must leave them.
        var (cache, sp) = Build();
        await using var _ = sp;
        var calls = 0;
        ValueTask<int> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<int>(1);
        }

        await cache.GetOrCreateLeaderboardAsync("lb:global:week:10:10", Factory, default);
        await cache.InvalidateFeedsAsync(default);
        await cache.GetOrCreateLeaderboardAsync("lb:global:week:10:10", Factory, default);

        calls.Should().Be(1, "the feed tag must not evict leaderboard entries");
    }
}
