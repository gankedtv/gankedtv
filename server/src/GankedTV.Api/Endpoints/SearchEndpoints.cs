using System.Security.Claims;
using System.Text.RegularExpressions;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Search;
using GankedTV.Api.Contracts.Users;
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

    // Matches contiguous runs of Unicode letters/digits. Everything else (whitespace,
    // hyphens, punctuation, tsquery operators like ! & | : *) acts as a separator, which
    // is what we want: "Counter-Strike 2" must tokenize to ["counter", "strike", "2"] to
    // align with what `to_tsvector('simple', …)` stores. The previous Split+Where chain
    // fused punctuation-separated words into one token (e.g. "CounterStrike"), which then
    // didn't match the split lexemes in the tsvector.
    private static readonly Regex TokenRegex = new(
        @"[\p{L}\p{N}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/search", Search);
        return app;
    }

    // Turns user input into a safe `tsquery` expression with prefix-matching on every
    // token (the `:*` suffix). Sanitization is by allowlist — only runs of Unicode
    // letters/digits become tokens, so tsquery operator injection (`!`, `&`, `|`, `(`,
    // `)`, `:`, `*`, `<->`) is structurally impossible.
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
        var tokens = TokenRegex.Matches(input)
            .Select(m => $"{m.Value}:*")
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
        if (type is not (null or "all" or "clips" or "games" or "users"))
        {
            return ProblemResults.BadRequest("invalid_type", "type must be all, clips, games, or users");
        }

        var tsQuery = BuildPrefixTsQuery(q);
        var clampedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var includeClips = type is null or "all" or "clips";
        var includeGames = type is null or "all" or "games";
        var includeUsers = type is null or "all" or "users";

        // Users match by ILIKE on the raw input, not the tsquery: usernames may consist
        // entirely of characters the tokenizer drops (e.g. "__x__"), so the users leg
        // must not be gated on tsQuery being non-null.
        var users = includeUsers
            ? (IReadOnlyList<UserSummary>)await SearchUsersAsync(q, clampedLimit, db, ct)
            : Array.Empty<UserSummary>();

        // Sanitized-empty input (e.g. q="!&|" or q="...") behaves like "no matches"
        // rather than 400 — the caller passed a non-blank string, it just contained
        // nothing tokenizable.
        if (tsQuery is null)
        {
            return Results.Ok(new SearchResponse(
                Array.Empty<ClipFeedItem>(),
                Array.Empty<GameListItem>(),
                users));
        }

        var clips = includeClips
            ? await SearchClipsAsync(tsQuery, clampedLimit, principal, db, storage, s3, ct)
            : Array.Empty<ClipFeedItem>();

        var games = includeGames
            ? (IReadOnlyList<GameListItem>)await SearchGamesAsync(tsQuery, clampedLimit, db, ct)
            : Array.Empty<GameListItem>();

        return Results.Ok(new SearchResponse(clips, games, users));
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
            .WherePublicReady()
            .Where(c => c.SearchVector.Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
            .OrderByDescending(c => c.SearchVector.Rank(EF.Functions.ToTsQuery("simple", tsQuery)))
            .ThenByDescending(c => c.CreatedAt)
            .Include(c => c.User)
            .Include(c => c.Game)
            .Take(limit)
            .ToListAsync(ct);

        return await ClipsReadEndpoints.ProjectFeedItemsAsync(rows, principal, db, storage, s3, ct);
    }

    private static Task<List<UserSummary>> SearchUsersAsync(
        string q,
        int limit,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        // Usernames are single short tokens, so substring ILIKE (prefix matches ranked
        // first) beats full-text search here. LIKE metacharacters are escaped the same
        // way as the games catalog search. Banned creators stay findable-by-nobody.
        var trimmed = q.Trim()
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");
        var prefix = $"{trimmed}%";
        var contains = $"%{trimmed}%";

        return db.Users.AsNoTracking()
            .Where(u => u.BannedAt == null && EF.Functions.ILike(u.Username, contains, @"\"))
            .OrderByDescending(u => EF.Functions.ILike(u.Username, prefix, @"\"))
            .ThenBy(u => u.Username)
            .Take(limit)
            .Select(u => new UserSummary(u.Id, u.Username, u.AvatarUrl))
            .ToListAsync(ct);
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
