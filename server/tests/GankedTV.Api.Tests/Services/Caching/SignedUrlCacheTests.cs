using FluentAssertions;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace GankedTV.Api.Tests.Services.Caching;

// Against a real HybridCache (L1-only, mirroring dev / a Redis outage). The property under test
// is URL *stability*: SigV4 stamps the signing time into the query string, so an un-memoised
// presign produces a different URL — and therefore a browser cache miss — on every request.
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
        // How a re-cut invalidates a viewer's cached poster: the object key is unchanged, so the
        // only thing that can bust the browser cache is a different URL.
        var (cache, _, sp) = Build();
        await using var __ = sp;

        var before = await cache.GetOrCreateAsync("thumbnails", "u/c.jpg", 0, default);
        var after = await cache.GetOrCreateAsync("thumbnails", "u/c.jpg", 1, default);

        after.Should().NotBe(before);
    }

    [Fact]
    public void CachedCopyNeverOutlivesTheSignature()
    {
        // Two ordering rules. A URL handed out at the last moment of the memo window must still
        // be valid for a while after, or the tail of every window serves URLs about to 403. And
        // max-age must not outlive the signature, or a browser keeps serving a URL that no longer
        // works. The lifetime also stays at the historical one hour on purpose: a longer
        // signature widens the window in which a leaked private-clip poster URL still resolves.
        SignedUrlCache.UrlLifetime.Should().Be(TimeSpan.FromHours(1));
        SignedUrlCache.CacheControlHeader.Should().Be("public, max-age=2700");
        TimeSpan.FromSeconds(2700).Should().BeLessThan(SignedUrlCache.UrlLifetime);
    }
}
