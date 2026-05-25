using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.RateLimiting;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.Caching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Clips;

// Rate-limit policy for clip mutation endpoints (POST /clips, PATCH/DELETE /clips/{id},
// the upload-url and complete steps, and the like/unlike pair). Kept out of Program.cs
// so the policy + partition logic stay inside the coverage denominator — Program.cs is
// excluded per CLAUDE.md.
public static class ClipsRateLimiting
{
    public const string ClipsWritePolicy = "clips-write";
    public const string ClipsViewPolicy = "clips-view";

    // 30 writes per minute. Per-user when the caller is authenticated, per-IP otherwise.
    // Fixed window (not sliding) — same shape as the credentials policy, no extra state.
    //
    // The 30 covers the WHOLE clip-write surface AND the moderation-report submission
    // endpoints — every endpoint that attaches this policy shares one bucket per user:
    // POST /clips, POST /clips/{id}/upload-url, POST /clips/{id}/complete, PATCH /clips/{id},
    // DELETE /clips/{id}, POST /clips/{id}/like, DELETE /clips/{id}/like,
    // POST /clips/{id}/report, POST /comments/{id}/report, POST /users/{id}/report.
    // A happy-path upload burns 3 (create + upload-url + complete), so the floor is ~10 clips/min
    // per user before the bucket exhausts. Reports share the same bucket on purpose: it caps
    // mass-report abuse without needing a second per-user keyed limiter. Pinned by
    // ClipsRateLimitTests.MixedWrites_ShareBucket.
    //
    // Enforced cluster-wide via RedisRateLimiterFactory when REDIS_URL is set — all pods share
    // one Redis bucket per partition key, so the 30/min ceiling holds regardless of pod count.
    // Without Redis (or during a Redis outage) it degrades to the original in-process fixed
    // window, which is per-pod — fine for single-instance dev.
    public const int WritePermitLimit = 30;
    public static readonly TimeSpan WriteWindow = TimeSpan.FromMinutes(1);

    // POST /clips/{id}/view is anonymous-friendly, so the bucket is per-IP only: a user-keyed
    // partition wouldn't bound abuse from logged-out clients (the dominant case for view pings).
    // 20/min is generous for legitimate playback (one view-ping per clip per 30 min on the dedup
    // window above this layer) but tight enough that a single host can't run a write storm.
    public const int ViewPermitLimit = 20;
    public static readonly TimeSpan ViewWindow = TimeSpan.FromMinutes(1);

    // Machine-readable code stamped into the ProblemDetails extensions when any policy
    // rejects. Mirrors the convention from ProblemResults.* used elsewhere in the API.
    public const string RateLimitedCode = "rate_limited";

    public static RateLimiterOptions AddClipsWritePolicy(this RateLimiterOptions options)
    {
        // OnRejected is a single global handler on RateLimiterOptions — there's no per-policy
        // slot. Wiring it here means every limiter rejection (this policy AND the credentials
        // policy registered in AuthRateLimiting) share one RFC 7807 envelope with
        // `code = "rate_limited"`, matching every other 4xx body the API emits.
        //
        // Sets `Retry-After` (seconds) from the lease metadata when available so well-behaved
        // clients (and the SPA) can back off instead of polling blind. RFC 6585 §4.
        options.OnRejected = static (ctx, _) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                // Round up to whole seconds, floored at 1. The in-process FixedWindowRateLimiter
                // fallback reports the *remaining* window as a sub-second TimeSpan, so a plain
                // (int) cast would emit `Retry-After: 0` late in a window — and RFC 7231 lets
                // clients retry immediately on 0, defeating the limiter. (The Redis path already
                // rounds up, so this is a no-op there.)
                var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                ctx.HttpContext.Response.Headers.RetryAfter =
                    seconds.ToString(CultureInfo.InvariantCulture);
            }
            return new ValueTask(ProblemResults.TooManyRequests(RateLimitedCode)
                .ExecuteAsync(ctx.HttpContext));
        };

        options.AddPolicy<string>(ClipsWritePolicy, ctx =>
        {
            // RedisRateLimiterFactory returns a Redis-backed limiter (shared across pods) when
            // REDIS_URL is configured, else the in-process fixed window. The framework caches
            // the returned limiter per partition key, so the factory runs once per bucket.
            var key = ResolvePartitionKey(ctx);
            var factory = ctx.RequestServices.GetRequiredService<RedisRateLimiterFactory>();
            return RateLimitPartition.Get(key, _ => factory.Create(ClipsWritePolicy, key, WritePermitLimit, WriteWindow));
        });
        return options;
    }

    public static RateLimiterOptions AddClipsViewPolicy(this RateLimiterOptions options)
    {
        options.AddPolicy<string>(ClipsViewPolicy, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ResolveIpPartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = ViewPermitLimit,
                Window = ViewWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
        return options;
    }

    // Pure per-IP key: the view endpoint is anonymous, so authenticated callers and
    // anonymous ones share the same per-host bucket. Fallback "unknown" guarantees a
    // total function — a missing RemoteIpAddress (test harness, broken proxy) must not
    // collapse every caller into one bucket and bypass the limit.
    internal static string ResolveIpPartitionKey(HttpContext ctx) =>
        $"ip:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    // Internal so unit tests can exercise the fallback branches that the HTTP-level integration
    // tests can't reach (RequireAuthorization rejects pre-limiter, so the per-IP and
    // NameIdentifier paths are unreachable through real requests).
    //
    // RequireAuthorization() on every /clips write group ensures HttpContext.User is populated
    // before the limiter runs (UseAuthentication is wired before UseRateLimiter in Program.cs).
    // The IP / "unknown" fallbacks remain so the partition function is total — a misconfiguration
    // that drops the auth middleware must not collapse every caller into a single bucket and
    // bypass the limit.
    internal static string ResolvePartitionKey(HttpContext ctx)
    {
        var sub = ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return !string.IsNullOrEmpty(sub)
            ? $"u:{sub}"
            : $"ip:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
