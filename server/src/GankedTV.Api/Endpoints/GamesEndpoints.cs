using System.Security.Claims;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Data;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class GamesEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public static IEndpointRouteBuilder MapGamesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games");
        group.MapGet("/", GetGames);
        group.MapGet("/{slug}", GetBySlug);
        group.MapGet("/{slug}/clips", GetClipsForGame);
        return app;
    }

    private static async Task<IResult> GetGames(
        string? search,
        int? limit,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var query = db.Games.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Trim, then escape LIKE metacharacters so a user typing "100%" doesn't
            // turn into a wildcard match. Backslash is escaped first to avoid
            // double-escaping the escapes themselves.
            var trimmed = search.Trim()
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");
            var pattern = $"%{trimmed}%";
            query = query.Where(g =>
                EF.Functions.ILike(g.Name, pattern, @"\")
                || EF.Functions.ILike(g.Slug, pattern, @"\"));
        }

        var rows = await query
            .OrderBy(g => g.Name)
            .Take(clampedLimit)
            .Select(g => new GameListItem(g.Id, g.Name, g.Slug, g.Tag, g.CoverUrl))
            .ToListAsync(ct);

        return Results.Ok(rows);
    }

    private static async Task<IResult> GetBySlug(
        string slug,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        // One round-trip: project the entity together with a correlated COUNT scoped
        // to the same visibility/status filter the clips list uses, so the header
        // count matches the number of clips a user can actually scroll through.
        var row = await db.Games.AsNoTracking()
            .Where(g => g.Slug == slug)
            .Select(g => new
            {
                Game = g,
                ClipCount = db.Clips.Count(c =>
                    c.GameId == g.Id && c.Visibility == "public" && c.Status == "ready"),
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? ProblemResults.NotFound("not_found")
            : Results.Ok(row.Game.ToDetail(row.ClipCount));
    }

    private static async Task<IResult> GetClipsForGame(
        string slug,
        string? cursor,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        // Distinguish "no such game" (404) from "game exists but has no clips" (200, empty page)
        // so the client can pick the right empty state.
        var gameId = await db.Games.AsNoTracking()
            .Where(g => g.Slug == slug)
            .Select(g => (int?)g.Id)
            .FirstOrDefaultAsync(ct);

        if (gameId is null)
            return ProblemResults.NotFound("not_found");

        var baseQuery = db.Clips.AsNoTracking()
            .Where(c => c.GameId == gameId && c.Visibility == "public" && c.Status == "ready");

        var response = await ClipsReadEndpoints.BuildFeedPageAsync(
            baseQuery, cursor, limit, principal, db, storage, s3, ct);
        return Results.Ok(response);
    }
}
