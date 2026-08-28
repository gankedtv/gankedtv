using FluentAssertions;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace GankedTV.Api.Tests.Services.Caching;

// Against a real HybridCache (L1-only, as in dev). The property under test is URL stability:
// SigV4 stamps the signing time, so an un-memoised presign is a cache miss every request.
public class SignedUrlCacheTests
{
    private static (SignedUrlCache cache, IObjectStorageService storage, ServiceProvider sp) Build()
    {
        var storage = Substitute.For<IObjectStorageService>();
        var signed = 0;
        storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(ci => $"https://cdn.test/{ci.ArgAt<string>(1)}?sig={++signed}");

        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        return (new SignedUrlCache(sp.GetRequiredService<HybridCache>(), storage), storage, sp);
    }

    [Fact]
    public async Task SameKey_ReturnsTheIdenticalUrl()
    {
        var (cache, storage, sp) = Build();
        await using var _ = sp;

        var first = await cache.GetOrCreateAsync("thumbnails", "u/c.jpg", 0, default);
        var second = await cache.GetOrCreateAsync("thumbnails", "u/c.jpg", 0, default);

        second.Should().Be(first, "a rotating URL is a guaranteed browser cache miss");
        storage.Received(1).GetPresignedGetUrl("thumbnails", "u/c.jpg", Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task DifferentKeys_GetDifferentUrls()
    {
        var (cache, _, sp) = Build();
        await using var __ = sp;

        var a = await cache.GetOrCreateAsync("thumbnails", "u/a.jpg", 0, default);
        var b = await cache.GetOrCreateAsync("thumbnails", "u/b.jpg", 0, default);

        b.Should().NotBe(a);
    }

    [Fact]
    public async Task DifferentBuckets_GetDifferentUrls()
    {
        var (cache, _, sp) = Build();
        await using var __ = sp;

        var a = await cache.GetOrCreateAsync("thumbnails", "u/c.jpg", 0, default);
        var b = await cache.GetOrCreateAsync("clips", "u/c.jpg", 0, default);

        b.Should().NotBe(a);
    }

    [Fact]
    public async Task BumpedVersion_MintsAFreshUrl()
    {
        // How a re-cut busts the browser cache: the key is unchanged, so the URL must differ.
        var (cache, _, sp) = Build();
        await using var __ = sp;

        var before = await cache.GetOrCreateAsync("thumbnails", "u/c.jpg", 0, default);
        var after = await cache.GetOrCreateAsync("thumbnails", "u/c.jpg", 1, default);

        after.Should().NotBe(before);
    }

    [Fact]
    public void CachedCopyNeverOutlivesTheSignature()
    {
        // One hour on purpose: longer widens the window in which a leaked private-clip poster
        // URL still resolves. `private` for the same reason — the bucket isn't anonymous-read.
        SignedUrlCache.UrlLifetime.Should().Be(TimeSpan.FromHours(1));
        SignedUrlCache.CacheControlHeader.Should().Be("private, max-age=900");

        // Worst case for one URL: minted at T, still handed out at T+MemoLifetime, folded into a
        // feed page cached for another FeedCacheTtl, then sat in a browser for max-age — and
        // thumbnails are lazy, so the fetch can be later still. All of it inside the signature.
        var feedCacheTtl = TimeSpan.FromSeconds(60);
        var worstCase = SignedUrlCache.MemoLifetime + feedCacheTtl + TimeSpan.FromSeconds(900);
        worstCase.Should().BeLessThanOrEqualTo(SignedUrlCache.UrlLifetime);
        (SignedUrlCache.UrlLifetime - worstCase).Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromMinutes(10), "a lazily-scrolled poster needs slack beyond the arithmetic");
    }
}
