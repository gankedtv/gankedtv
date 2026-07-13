using System.Threading.RateLimiting;
using GankedTV.Api.Clips;
using GankedTV.Api.Services.Caching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Endpoints;

// Rate limit for GET /games. A search miss from an authenticated caller reaches out to IGDB and
// can insert catalog rows + mirror covers to S3, so the browse/typeahead endpoint needs a ceiling
// on how many distinct terms one caller can drive. Kept out of Program.cs so it stays inside the
// coverage denominator.
public static class GamesRateLimiting
{
    public const string GamesSearchPolicy = "games-search";

    // 60/min per caller. A 200ms-debounced typeahead emits a handful of requests per name, so
    // this is generous for a human and tight enough to bound IGDB fan-out from one account.
    public const int PermitLimit = 60;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimiterOptions AddGamesSearchPolicy(this RateLimiterOptions options)
    {
        // Redis-backed when REDIS_URL is set, so the ceiling holds cluster-wide — IGDB's 4 req/s
        // budget is shared by every pod. Rejections reuse the global OnRejected from
        // ClipsRateLimiting (RFC 7807 429 + Retry-After).
        options.AddPolicy<string>(GamesSearchPolicy, ctx =>
        {
            var key = ClipsRateLimiting.ResolvePartitionKey(ctx);
            var factory = ctx.RequestServices.GetRequiredService<RedisRateLimiterFactory>();
            return RateLimitPartition.Get(key, _ => factory.Create(GamesSearchPolicy, key, PermitLimit, Window));
        });
        return options;
    }
}
