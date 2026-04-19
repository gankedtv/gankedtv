using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

public static class LikesEndpoints
{
    public static IEndpointRouteBuilder MapLikesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips").RequireAuthorization();
        group.MapPost("/{id:guid}/like", LikeClip);
        group.MapDelete("/{id:guid}/like", UnlikeClip);
        return app;
    }

    private static async Task<IResult> LikeClip(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var clipExists = await db.Clips.AnyAsync(c => c.Id == id, ct);
        if (!clipExists)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        var already = await db.Likes.AnyAsync(l => l.UserId == userId && l.ClipId == id, ct);
        if (!already)
        {
            db.Likes.Add(new Like
            {
                UserId = userId,
                ClipId = id,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            // Raw-SQL increment keeps the counter race-free against concurrent likers on
            // a different row (we already hold the (user,clip) row uniquely via the PK).
            await db.Clips.Where(c => c.Id == id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.LikeCount, c => c.LikeCount + 1),
                    ct);
        }

        var count = await db.Clips.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.LikeCount)
            .FirstAsync(ct);

        await tx.CommitAsync(ct);

        return Results.Ok(new LikeResponse(count, true));
    }

    private static async Task<IResult> UnlikeClip(
        Guid id,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var clipExists = await db.Clips.AnyAsync(c => c.Id == id, ct);
        if (!clipExists)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        var like = await db.Likes.FirstOrDefaultAsync(
            l => l.UserId == userId && l.ClipId == id, ct);

        if (like is not null)
        {
            db.Likes.Remove(like);
            await db.SaveChangesAsync(ct);
            // `LikeCount > 0` guard provides the ≥ 0 clamp required by the acceptance
            // criteria: if the counter is already 0 (data drift, manual row insert, etc.)
            // the decrement is a no-op rather than introducing a negative count.
            await db.Clips.Where(c => c.Id == id && c.LikeCount > 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.LikeCount, c => c.LikeCount - 1),
                    ct);
        }

        var count = await db.Clips.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.LikeCount)
            .FirstAsync(ct);

        await tx.CommitAsync(ct);

        return Results.Ok(new LikeResponse(count, false));
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }
}
