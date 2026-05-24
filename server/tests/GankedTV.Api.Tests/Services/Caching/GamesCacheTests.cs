using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Services.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GankedTV.Api.Tests.Services.Caching;

// Exercises GamesCache against a real HybridCache (L1-only — no Redis). Verifies the cache-hit
// behaviour the games endpoints rely on, plus that the cached payloads survive the System.Text.Json
// round-trip the Redis L2 performs (L1-only tests store by reference and wouldn't catch that).
public class GamesCacheTests
{
    private static (GamesCache cache, ServiceProvider sp) Build()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        return (new GamesCache(sp.GetRequiredService<HybridCache>()), sp);
    }

    [Fact]
    public async Task GetOrCreateListAsync_SecondCall_ServedFromCache()
    {
        var (cache, sp) = Build();
        await using var _ = sp;
        var calls = 0;
        ValueTask<IReadOnlyList<GameListItem>> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<IReadOnlyList<GameListItem>>(
                new List<GameListItem> { new(1, "Valorant", "valorant", "VAL", null) });
        }

        var first = await cache.GetOrCreateListAsync("games:list:hasClips=true:20", Factory, default);
        var second = await cache.GetOrCreateListAsync("games:list:hasClips=true:20", Factory, default);

        calls.Should().Be(1);
        first.Should().ContainSingle();
        second[0].Slug.Should().Be("valorant");
    }

    [Fact]
    public async Task GetOrCreateDetailAsync_SecondCall_ServedFromCache()
    {
        var (cache, sp) = Build();
        await using var _ = sp;
        var calls = 0;
        ValueTask<GameDetail?> Factory(CancellationToken _)
        {
            calls++;
            return new ValueTask<GameDetail?>(new GameDetail(7, "Counter-Strike 2", "cs2", "CS2", "http://x/cs2.jpg", 42));
        }

        var first = await cache.GetOrCreateDetailAsync("games:detail:cs2", Factory, default);
        var second = await cache.GetOrCreateDetailAsync("games:detail:cs2", Factory, default);

        calls.Should().Be(1);
        first!.ClipCount.Should().Be(42);
        second!.Slug.Should().Be("cs2");
    }

    [Fact]
    public void GamesCatalogDtos_RoundTripThroughSystemTextJson()
    {
        // HybridCache serializes L2 entries with System.Text.Json; confirm the cached payloads
        // (records, one with a null CoverUrl) reconstruct faithfully.
        var list = new List<GameListItem>
        {
            new(1, "Valorant", "valorant", "VAL", "http://cdn/val.jpg"),
            new(2, "Dota 2", "dota-2", "DOTA2", null),
        };
        var detail = new GameDetail(3, "Apex Legends", "apex-legends", "APEX", null, 99);

        JsonSerializer.Deserialize<List<GameListItem>>(JsonSerializer.Serialize(list))
            .Should().BeEquivalentTo(list);
        JsonSerializer.Deserialize<GameDetail>(JsonSerializer.Serialize(detail))
            .Should().BeEquivalentTo(detail);
    }
}
