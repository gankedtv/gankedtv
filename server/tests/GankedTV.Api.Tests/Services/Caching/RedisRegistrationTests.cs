using FluentAssertions;
using GankedTV.Api.Services.Caching;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GankedTV.Api.Tests.Services.Caching;

// AddGankedCaching wires DataProtection key persistence to Redis only when REDIS_URL is configured,
// so keys survive restarts (and are shared across replicas) instead of falling back to an ephemeral
// in-memory keyring. Without Redis (local dev) it stays at the default.
public class RedisRegistrationTests
{
    private static KeyManagementOptions ResolveKeyOptions(string? redisUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddGankedCaching(new RedisOptions { Url = redisUrl });
        // Resolving the options runs the configure delegate but never invokes the multiplexer
        // factory (RedisXmlRepository's IDatabase factory is lazy), so no Redis is contacted.
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
    }

    [Fact]
    public void AddGankedCaching_WithRedisConfigured_PersistsDataProtectionKeysToRedis()
    {
        var km = ResolveKeyOptions("redis://localhost:6379");
        km.XmlRepository.Should().BeOfType<RedisXmlRepository>();
    }

    [Fact]
    public void AddGankedCaching_WithoutRedis_LeavesDataProtectionAtDefault()
    {
        var km = ResolveKeyOptions(redisUrl: null);
        (km.XmlRepository as RedisXmlRepository).Should().BeNull();
    }

    [Fact]
    public void AddGankedCaching_WithMalformedRedisUrl_LeavesDataProtectionAtDefault()
    {
        // A non-URL value fails TryBuildConfiguration's Uri.TryCreate, degrading to the in-process
        // fallback (no Redis) — so DP must NOT be wired to Redis.
        var km = ResolveKeyOptions("not-a-url");
        (km.XmlRepository as RedisXmlRepository).Should().BeNull();
    }
}
