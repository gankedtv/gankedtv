using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Users;
using GankedTV.Api.Data;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return ProblemResults.NotFound("not_found");
        }

        // Case-insensitive equality via LOWER(...). Avoid EF.Functions.ILike here — `%` and `_`
        // would be interpreted as PG wildcards, letting `/users/a%` match any name starting with a
        // and legitimate `_` in a username become a wildcard.
        var usernameLower = username.ToLowerInvariant();
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == usernameLower, ct);

        if (user is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        var clips = await db.Clips.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.Visibility == "public" && c.Status == "ready")
            .OrderByDescending(c => c.CreatedAt)
            .Include(c => c.User)
            .Take(UserClipsPageSize)
            .ToListAsync(ct);

        var likedIds = await ClipsReadEndpoints.LoadLikedClipIdsAsync(
            db, principal, clips.Select(c => c.Id), ct);

        var thumbnailsBucket = s3.Value.ThumbnailsBucket;
        var clipDtos = clips
            .Select(c => c.ToFeedItem(
                ClipsReadEndpoints.BuildThumbnailUrl(storage, thumbnailsBucket, c.ThumbnailKey),
                likedIds.Contains(c.Id)))
            .ToList();

        return Results.Ok(user.ToProfile(clipDtos));
    }
}
