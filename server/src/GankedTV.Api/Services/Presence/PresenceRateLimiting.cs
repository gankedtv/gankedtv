using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace GankedTV.Api.Services.Presence;

// Per-IP rate limit for GET /presence/summary. The endpoint is anonymous and records a viewer on
// every call, so without a limit a client could spam distinct ?cid= values to inflate the online
// count (and grow the Redis set within the window). Mirrors the anonymous clip-view limiter: a
// per-IP fixed window, in-process (per-pod is fine — this bounds abuse, it isn't a correctness
// control). Kept out of Program.cs so it stays inside the coverage denominator.
public static class PresenceRateLimiting
{
    public const string PresencePolicy = "presence-summary";

    // 60/min per IP: a 30–60s nav poll is ~1–2/min per browser, so this leaves generous headroom
    // for many tabs / several users behind one NAT while capping how many fake cids one host can
    // register per window.
    public const int PermitLimit = 60;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimiterOptions AddPresencePolicy(this RateLimiterOptions options)
    {
        // Rejections reuse the global OnRejected handler wired in ClipsRateLimiting (RFC 7807 429
        // with Retry-After), so no per-policy handler is needed here.
        options.AddPolicy<string>(PresencePolicy, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                $"ip:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = PermitLimit,
                    Window = Window,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }));
        return options;
    }
}
