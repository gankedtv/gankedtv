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

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/search", Search);
        return app;
    }

    // Turns user input into a safe `tsquery` expression with prefix-matching on every
    // token (the `:*` suffix). Sanitization is by allowlist — anything that isn't a
    // Unicode letter or digit is dropped, which both prevents tsquery operator injection
    // (`!`, `&`, `|`, `(`, `)`, `:`, `*`, `<->`) and naturally splits on punctuation so
    // "Counter-Strike 2" tokenizes the same way the simple dictionary does.
    //
    // Why `to_tsquery` + manual `:*` instead of `plainto_tsquery`:
    //   plainto_tsquery has no prefix-match support, so typing "valo" wouldn't match the
    //   lexeme "valorant", and typing "04" would only match if the full token "04" appears
    //   — fine here, but typing "0" wouldn't match anything. Building the query manually
    //   gives us a single search path that handles 1-char, multi-char, and numeric tokens
    //   identically, and removes the need for the ILIKE prefix-fallback that previously
    //   only matched title prefixes (which is why "04" didn't find "Seed Clip 04").
    //
    // Returns null when the sanitized input has no usable tokens — caller treats that
    // as "no results" rather than constructing an empty tsquery (which would 500).
    internal static string? BuildPrefixTsQuery(string input)
    {
        var tokens = input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string([.. t.Where(char.IsLetterOrDigit)]))
            .Where(t => t.Length > 0)
            .Select(t => $"{t}:*")
            .ToArray();
        return tokens.Length == 0 ? null : string.Join(" & ", tokens);
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

        var tsQuery = BuildPrefixTsQuery(q);
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var includeClips = type is null or "all" or "clips";
        var includeGames = type is null or "all" or "games";

        // Sanitized-empty input (e.g. q="!&|" or q="...") behaves like "no matches"
        // rather than 400 — the caller passed a non-blank string, it just contained
        // nothing tokenizable.
        if (tsQuery is null)
        {
            return Results.Ok(new SearchResponse(
                Array.Empty<ClipFeedItem>(),
                Array.Empty<GameListItem>()));
        }

        var clips = includeClips
            ? await SearchClipsAsync(tsQuery, clampedLimit, principal, db, storage, s3, ct)
            : Array.Empty<ClipFeedItem>();

        var games = includeGames
            ? (IReadOnlyList<GameListItem>)await SearchGamesAsync(tsQuery, clampedLimit, db, ct)
            : Array.Empty<GameListItem>();

        return Results.Ok(new SearchResponse(clips, games));
    }

    private static async Task<IReadOnlyList<ClipFeedItem>> SearchClipsAsync(
        string tsQuery,
        int limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        // Two ToTsQuery calls (one in Where, one in OrderBy) — Postgres folds identical
        // immutable expressions, and the GIN index still drives the @@ predicate.
        var rows = await db.Clips.AsNoTracking()
            .Where(c => c.Visibility == "public" && c.Status == "ready")
            .Where(c => c.SearchVector.Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
            .OrderByDescending(c => c.SearchVector.Rank(EF.Functions.ToTsQuery("simple", tsQuery)))
            .ThenByDescending(c => c.CreatedAt)
            .Include(c => c.User)
            .Include(c => c.Game)
            .Take(limit)
            .ToListAsync(ct);

        return await ClipsReadEndpoints.ProjectFeedItemsAsync(rows, principal, db, storage, s3, ct);
    }

    private static Task<List<GameListItem>> SearchGamesAsync(
        string tsQuery,
        int limit,
        GankedTvDbContext db,
        CancellationToken ct) =>
        db.Games.AsNoTracking()
            .Where(g => g.SearchVector.Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
            .OrderByDescending(g => g.SearchVector.Rank(EF.Functions.ToTsQuery("simple", tsQuery)))
            .ThenBy(g => g.Name)
            .Take(limit)
            .Select(g => new GameListItem(g.Id, g.Name, g.Slug, g.Tag, g.CoverUrl))
            .ToListAsync(ct);
}
