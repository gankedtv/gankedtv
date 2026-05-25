using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

/// <summary>
/// Dev-only helpers for local testing without OAuth credentials. These endpoints are
/// only mapped when <see cref="IHostEnvironment.IsDevelopment"/> is true, so they are
/// never exposed in Staging/Production regardless of how the host is configured.
/// </summary>
public static class DevAuthEndpoints
{
    // `Role` lets the caller mint a token for an arbitrary role (e.g. "admin") without
    // having to run `make seed` and remember credentials. The endpoint is Development-only
    // so the "elevate to admin via API" affordance is intended — never reachable in prod.
    public sealed record DevTokenRequest(string? Username, string? Role);
    public sealed record DevTokenResponse(string Token, string Refresh, Guid UserId, string Username, string Role);

    public static IEndpointRouteBuilder MapDevAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/token", IssueDevToken);
        return app;
    }

    private static async Task<IResult> IssueDevToken(
        [FromBody] DevTokenRequest? req,
        GankedTvDbContext db,
        IJwtService jwt,
        IRefreshTokenService refreshTokens,
        CancellationToken ct)
    {
        var raw = req?.Username ?? "dev-user";
        var username = UsernameGenerator.Slugify(raw);
        // Unknown role values silently fall back to "user" so a typo in the request body
        // can't accidentally mint an admin token — explicit allow-list, not deny-list.
        var role = UserRoles.IsValid(req?.Role ?? string.Empty) ? req!.Role! : UserRoles.User;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            var now = DateTimeOffset.UtcNow;
            user = new User
            {
                Username = username,
                Email = $"{username}@dev.local",
                Role = role,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }
        else if (req?.Role is not null && user.Role != role)
        {
            // Re-asserting the requested role on every call means the "Sign in as seedadmin"
            // button keeps working even if a contributor manually demoted the seeded row.
            // Dev-only, so privilege escalation here is intentional.
            user.Role = role;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var token = jwt.Issue(user);
        var refresh = await refreshTokens.IssueAsync(user.Id, ct);
        return Results.Ok(new DevTokenResponse(token, refresh, user.Id, user.Username, user.Role));
    }
}
