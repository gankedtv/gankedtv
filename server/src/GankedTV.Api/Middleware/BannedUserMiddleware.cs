using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Data;
using GankedTV.Api.Problems;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Middleware;

// Rejects every request authenticated as a banned user, regardless of token freshness — so a
// ban takes effect immediately rather than waiting for the JWT to expire. Sits between
// UseAuthentication and UseAuthorization in the pipeline (Program.cs); requests without a
// `sub` claim short-circuit before the DB lookup.
public sealed class BannedUserMiddleware : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var sub = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(sub, out var userId))
        {
            var db = context.RequestServices.GetRequiredService<GankedTvDbContext>();
            // AsNoTracking + a single bool projection — cheap one-column lookup per authed
            // request. Cluster-wide cache (Redis) is a future optimization; this is fast
            // enough for v1 (the index on users(id) is already there for the PK).
            var banned = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.BannedAt != null)
                .FirstOrDefaultAsync(context.RequestAborted);
            if (banned)
            {
                // 401 (not 403) on purpose: it routes the SPA through its existing
                // 401→/auth/refresh→onRefreshFailed flow, which clears the session and
                // redirects to login. The /auth/refresh endpoint independently rejects
                // banned accounts so the refresh fails cleanly. The `code=account_banned`
                // body keeps banned traffic distinguishable from regular token-expiry
                // 401s in problem-details logs and metrics.
                await ProblemResults.Unauthorized("account_banned").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
}
