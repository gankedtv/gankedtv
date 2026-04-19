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
    public sealed record DevTokenRequest(string? Username);
    public sealed record DevTokenResponse(string Token, string Refresh, Guid UserId, string Username);

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

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            var now = DateTimeOffset.UtcNow;
            user = new User
            {
                Username = username,
                Email = $"{username}@dev.local",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }

        var token = jwt.Issue(user);
        var refresh = await refreshTokens.IssueAsync(user.Id, ct);
        return Results.Ok(new DevTokenResponse(token, refresh, user.Id, user.Username));
    }
}
