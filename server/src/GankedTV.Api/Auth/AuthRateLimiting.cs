using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace GankedTV.Api.Auth;

// Centralises the rate-limit policy used by the credential auth endpoints
// (/auth/login, /auth/register). Kept in its own service so the policy and
// key-extraction logic stay inside the coverage denominator — Program.cs is
// excluded from coverage per CLAUDE.md.
public static class AuthRateLimiting
{
    public const string CredentialsPolicy = "auth-credentials";

    // 5 attempts per minute per remote IP. Fixed window (not sliding) — keeps the bucket
    // arithmetic in-process with no extra state. Returns 429 when exceeded; the SPA
    // surfaces this inline below the form. No queueing — failed limit checks short-circuit.
    public const int CredentialsPermitLimit = 5;
    public static readonly TimeSpan CredentialsWindow = TimeSpan.FromMinutes(1);

    public static RateLimiterOptions AddCredentialsPolicy(this RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy<string>(CredentialsPolicy, ctx =>
        {
            // PROD CAVEAT: Connection.RemoteIpAddress is the immediate peer. Behind a
            // reverse proxy or load balancer, that's the proxy itself — every request
            // collapses into one bucket and the limiter becomes a global shared lane.
            // Wire `app.UseForwardedHeaders(ForwardedHeaders.XForwardedFor)` (with a
            // KnownProxies / KnownNetworks allowlist so client-supplied X-Forwarded-For
            // can't spoof the bucket) before exposing this beyond localhost.
            //
            // Falling back to a stable bucket when no remote IP is observable (test rigs,
            // unrecognised proxies). Without a fallback, those callers would partition
            // by null and bypass the limit entirely.
            var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = CredentialsPermitLimit,
                Window = CredentialsWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        });
        return options;
    }
}
