using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Users;
using GankedTV.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

public static class UsersEndpoints
{
    private const int UserClipsPageSize = 20;

    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users");
        group.MapGet("/{username}", GetByUsername);
        return app;
    }

    private static async Task<IResult> GetByUsername(
        string username,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Results.NotFound(new { error = "not_found" });
        }

        // Case-insensitive equality via LOWER(...). Avoid EF.Functions.ILike here — `%` and `_`
        // would be interpreted as PG wildcards, letting `/users/a%` match any name starting with a
        // and legitimate `_` in a username become a wildcard.
        var usernameLower = username.ToLowerInvariant();
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == usernameLower, ct);

        if (user is null)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        var clips = await db.Clips.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.Visibility == "public" && c.Status == "ready")
            .OrderByDescending(c => c.CreatedAt)
            .Include(c => c.User)
            .Take(UserClipsPageSize)
            .ToListAsync(ct);

        var likedIds = await ClipsReadEndpoints.LoadLikedClipIdsAsync(
            db, principal, clips.Select(c => c.Id), ct);

        var clipDtos = clips.Select(c => c.ToFeedItem(likedIds.Contains(c.Id))).ToList();

        return Results.Ok(user.ToProfile(clipDtos));
    }
}
