using System.Security.Claims;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Search;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class SearchEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    // < 3 chars falls back to ILIKE prefix matching. `plainto_tsquery('simple', 'va')` would
    // produce a lexeme that only matches whole words, so a user typing the first 1–2 chars
    // of "Valorant" would get nothing without this fallback. 3 chars is the same cutoff
    // most prefix-tries use empirically — short enough to feel responsive, long enough to
    // be a useful word stem.
    private const int FullTextMinLength = 3;

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/search", Search);
        return app;
    }

    private static async Task<IResult> Search(
        string? q,
        string? type,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return ProblemResults.BadRequest("invalid_query", "q is required");
        }

        // Reject unknown types loudly rather than silently returning empty halves —
        // a client asking for `type=clip` (singular) would otherwise look like a "no
        // results" bug instead of a misuse of the contract.
        if (type is not (null or "all" or "clips" or "games"))
        {
            return ProblemResults.BadRequest("invalid_type", "type must be all, clips, or games");
        }

        var trimmed = q.Trim();
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var includeClips = type is null or "all" or "clips";
        var includeGames = type is null or "all" or "games";

        var clips = includeClips
            ? await SearchClipsAsync(trimmed, clampedLimit, principal, db, storage, s3, ct)
            : Array.Empty<ClipFeedItem>();

        var games = includeGames
            ? await SearchGamesAsync(trimmed, clampedLimit, db, ct)
            : Array.Empty<GameListItem>();

        return Results.Ok(new SearchResponse(clips, games));
    }

    private static async Task<IReadOnlyList<ClipFeedItem>> SearchClipsAsync(
        string trimmed,
        int limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var baseQuery = db.Clips.AsNoTracking()
            .Where(c => c.Visibility == "public" && c.Status == "ready");

        List<Clip> rows;
        if (trimmed.Length >= FullTextMinLength)
        {
            // PlainToTsQuery treats the input as a phrase, not raw tsquery syntax, so
            // metacharacters like ! & | < : * never reach the query parser. Two function calls
            // (one in Where, one in OrderBy) are fine — Postgres folds identical immutable
            // expressions and the GIN index drives the Matches predicate either way.
            rows = await baseQuery
                .Where(c => c.SearchVector.Matches(EF.Functions.PlainToTsQuery("simple", trimmed)))
                .OrderByDescending(c => c.SearchVector.Rank(EF.Functions.PlainToTsQuery("simple", trimmed)))
                .ThenByDescending(c => c.CreatedAt)
                .Include(c => c.User)
                .Include(c => c.Game)
                .Take(limit)
                .ToListAsync(ct);
        }
        else
        {
            var pattern = EscapeLikePattern(trimmed) + "%";
            rows = await baseQuery
                .Where(c => EF.Functions.ILike(c.Title, pattern, @"\"))
                .OrderByDescending(c => c.CreatedAt)
                .Include(c => c.User)
                .Include(c => c.Game)
                .Take(limit)
                .ToListAsync(ct);
        }

        return await ClipsReadEndpoints.ProjectFeedItemsAsync(rows, principal, db, storage, s3, ct);
    }

    private static async Task<IReadOnlyList<GameListItem>> SearchGamesAsync(
        string trimmed,
        int limit,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var baseQuery = db.Games.AsNoTracking();

        if (trimmed.Length >= FullTextMinLength)
        {
            return await baseQuery
                .Where(g => g.SearchVector.Matches(EF.Functions.PlainToTsQuery("simple", trimmed)))
                .OrderByDescending(g => g.SearchVector.Rank(EF.Functions.PlainToTsQuery("simple", trimmed)))
                .ThenBy(g => g.Name)
                .Take(limit)
                .Select(g => new GameListItem(g.Id, g.Name, g.Slug, g.Tag, g.CoverUrl))
                .ToListAsync(ct);
        }

        var pattern = EscapeLikePattern(trimmed) + "%";
        return await baseQuery
            .Where(g => EF.Functions.ILike(g.Name, pattern, @"\"))
            .OrderBy(g => g.Name)
            .Take(limit)
            .Select(g => new GameListItem(g.Id, g.Name, g.Slug, g.Tag, g.CoverUrl))
            .ToListAsync(ct);
    }

    // Mirrors GamesEndpoints' substring-search escape: backslash first (so it doesn't
    // double-escape the escapes we add next), then %/_ . Kept private here instead of
    // shared because the two endpoints have different match semantics (substring vs.
    // prefix) and conflating them would invite the wrong choice at the call site.
    private static string EscapeLikePattern(string input) =>
        input.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
