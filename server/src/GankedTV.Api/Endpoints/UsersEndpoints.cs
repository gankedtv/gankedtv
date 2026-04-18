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
            return Results.NotFound();
        }

        // Username index is unique but case-sensitive; match case-insensitively so `/users/AliCe`
        // works whether the OAuth-assigned slug is "alice" or "Alice".
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => EF.Functions.ILike(u.Username, username), ct);

        if (user is null)
        {
            return Results.NotFound();
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
