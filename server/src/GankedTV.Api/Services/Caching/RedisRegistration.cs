using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace GankedTV.Api.Services.Caching;

/// <summary>
/// Wires the caching + cluster-coordination stack, gated on whether Redis is configured.
/// Lives in a service (not Program.cs) so the conditional logic stays inside the coverage
/// denominator per CLAUDE.md.
///
/// Always registers <see cref="HybridCache"/>, <see cref="FeedCache"/>, and
/// <see cref="RedisRateLimiterFactory"/>. When <c>REDIS_URL</c> is set and parses, also
/// registers a shared <see cref="IConnectionMultiplexer"/> and attaches Redis as HybridCache's
/// L2 (the <c>IDistributedCache</c>) — so the same connection backs both caching and the
/// rate limiter. When it's unset/malformed, HybridCache runs L1-only and the limiter falls
/// back to in-process: local dev needs no Redis.
/// </summary>
public static class RedisRegistration
{
    public static IServiceCollection AddGankedCaching(this IServiceCollection services, RedisOptions options)
    {
        services.AddHybridCache();
        services.AddSingleton<IFeedCache, FeedCache>();
        services.AddSingleton<IGamesCache, GamesCache>();
        services.AddSingleton<RedisRateLimiterFactory>();

        if (options.TryBuildConfiguration(out var configuration))
        {
            // Lazy, container-owned singleton: Connect runs on first resolve (first cache op or
            // rate-limited request), not at boot — so a slow/unreachable Redis never delays
            // startup (AbortOnConnectFail=false also keeps it non-throwing). Registering the
            // factory (not a pre-built instance) lets the DI container dispose it on shutdown,
            // giving Redis a clean QUIT instead of a dropped TCP connection. Both the L2 cache and
            // the limiter resolve this same instance, so there's still exactly one connection.
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(configuration));
            services.AddStackExchangeRedisCache(_ => { });
            services.AddOptions<RedisCacheOptions>().Configure<IServiceProvider>((cacheOptions, sp) =>
                cacheOptions.ConnectionMultiplexerFactory =
                    () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>()));
        }

        return services;
    }
}
