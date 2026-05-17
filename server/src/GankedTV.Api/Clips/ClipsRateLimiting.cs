using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.RateLimiting;
using GankedTV.Api.Problems;
using Microsoft.AspNetCore.RateLimiting;

namespace GankedTV.Api.Clips;

// Rate-limit policy for clip mutation endpoints (POST /clips, PATCH/DELETE /clips/{id},
// the upload-url and complete steps, and the like/unlike pair). Kept out of Program.cs
// so the policy + partition logic stay inside the coverage denominator — Program.cs is
// excluded per CLAUDE.md.
public static class ClipsRateLimiting
{
    public const string ClipsWritePolicy = "clips-write";

    // 30 writes per minute. Per-user when the caller is authenticated, per-IP otherwise.
    // Fixed window (not sliding) — same shape as the credentials policy, no extra state.
    //
    // The 30 covers the WHOLE clip-write surface — every endpoint that attaches this policy
    // shares one bucket per user: POST /clips, POST /clips/{id}/upload-url, POST /clips/{id}/complete,
    // PATCH /clips/{id}, DELETE /clips/{id}, POST /clips/{id}/like, DELETE /clips/{id}/like.
    // A happy-path upload burns 3 (create + upload-url + complete), so the floor is ~10 clips/min
    // per user before the bucket exhausts. Pinned by ClipsRateLimitTests.MixedWrites_ShareBucket.
    //
    // SCALING CAVEAT: this is in-process, per-instance state. Once the API runs on more than
    // one pod the effective limit becomes 30 × pod_count per user. Phase 4 (issue #74 out-of-scope)
    // moves to a Redis-backed limiter for cluster-wide enforcement.
    public const int WritePermitLimit = 30;
    public static readonly TimeSpan WriteWindow = TimeSpan.FromMinutes(1);

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
                ctx.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            }
            return new ValueTask(ProblemResults.TooManyRequests(RateLimitedCode)
                .ExecuteAsync(ctx.HttpContext));
        };

        options.AddPolicy<string>(ClipsWritePolicy, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ResolvePartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = WritePermitLimit,
                Window = WriteWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
        return options;
    }

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
