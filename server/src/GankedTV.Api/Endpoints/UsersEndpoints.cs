using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Users;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
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

    // Case-insensitive username lookup shared by UsersEndpoints + FollowsEndpoints. Uses
    // LOWER(...) rather than EF.Functions.ILike so `%` and `_` in the path segment are
    // matched literally instead of acting as PG wildcards.
    internal static Task<User?> FindByUsernameAsync(
        GankedTvDbContext db, string username, CancellationToken ct)
    {
        var lower = username.ToLowerInvariant();
        return db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == lower, ct);
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

        var user = await FindByUsernameAsync(db, username, ct);

        if (user is null)
        {
            return ProblemResults.NotFound("not_found");
        }

        // Owner viewing their own profile sees public + unlisted + private clips so they can
        // find everything they uploaded; everyone else sees public only. Hidden (moderated)
        // clips stay invisible to everyone here — restoring them is an admin action, not
        // something the owner can route around by checking their own profile.
        var isOwner = principal.TryGetUserId(out var viewerId)
            && viewerId == user.Id;
        var clips = await db.Clips.AsNoTracking()
            .Where(c => c.UserId == user.Id
                && c.Status == ClipStatuses.Ready
                && (c.Visibility == ClipVisibilities.Public
                    || (isOwner && c.Visibility != ClipVisibilities.Hidden)))
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

        var followerCount = await db.Follows.AsNoTracking()
            .CountAsync(f => f.FolloweeId == user.Id, ct);
        var followingCount = await db.Follows.AsNoTracking()
            .CountAsync(f => f.FollowerId == user.Id, ct);

        // followedByMe is intentionally nullable: the issue spec requires "null / absent"
        // for unauthenticated callers (and we extend the same to self-views where the
        // field would be meaningless) rather than defaulting to `false`. With ASP.NET's
        // default Web JSON settings (no JsonIgnoreCondition.WhenWritingNull) this
        // serializes as `"followedByMe": null` — clients should treat null and absent
        // identically.
        bool? followedByMe = null;
        if (principal.TryGetUserId(out var callerId) && callerId != user.Id)
        {
            followedByMe = await db.Follows.AsNoTracking()
                .AnyAsync(f => f.FollowerId == callerId && f.FolloweeId == user.Id, ct);
        }

        return Results.Ok(user.ToProfile(clipDtos, followerCount, followingCount, followedByMe));
    }
}
