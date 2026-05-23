using Microsoft.Extensions.Caching.Hybrid;
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
            // AbortOnConnectFail=false (set in TryBuildConfiguration) means Connect returns a
            // multiplexer immediately even if Redis is down at boot; it reconnects in the
            // background. A single shared multiplexer backs both the L2 cache and the limiter.
            var multiplexer = ConnectionMultiplexer.Connect(configuration);
            services.AddSingleton<IConnectionMultiplexer>(multiplexer);
            services.AddStackExchangeRedisCache(o =>
                o.ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(multiplexer));
        }

        return services;
    }
}
