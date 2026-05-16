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
        options.OnRejected = static (ctx, _) =>
            new ValueTask(ProblemResults.TooManyRequests(RateLimitedCode)
                .ExecuteAsync(ctx.HttpContext));

        options.AddPolicy<string>(ClipsWritePolicy, ctx =>
        {
            // RequireAuthorization() on every /clips write group ensures HttpContext.User is
            // populated before the limiter runs (UseAuthentication is wired before
            // UseRateLimiter in Program.cs). We still fall through to IP / "unknown" so the
            // partition function is total — a misconfiguration that drops the auth middleware
            // must not collapse every caller into a single bucket and bypass the limit.
            var sub = ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var key = !string.IsNullOrEmpty(sub)
                ? $"u:{sub}"
                : $"ip:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = WritePermitLimit,
                Window = WriteWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        });
        return options;
    }
}
