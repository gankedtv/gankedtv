using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

public static class GamesEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public static IEndpointRouteBuilder MapGamesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/games");
        group.MapGet("/", GetGames);
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
            // Trim incoming search; case-insensitive ILIKE against Name and Slug.
            var pattern = $"%{search.Trim()}%";
            query = query.Where(g =>
                EF.Functions.ILike(g.Name, pattern) || EF.Functions.ILike(g.Slug, pattern));
        }

        var rows = await query
            .OrderBy(g => g.Name)
            .Take(clampedLimit)
            .Select(g => new GameListItem(g.Id, g.Name, g.Slug, g.Tag, g.CoverUrl))
            .ToListAsync(ct);

        return Results.Ok(rows);
    }
}
