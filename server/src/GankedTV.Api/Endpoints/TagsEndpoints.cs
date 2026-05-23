using System.Security.Claims;
using GankedTV.Api.Contracts.Tags;
using GankedTV.Api.Data;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Services.Tags;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

public static class TagsEndpoints
{
    private const int AutocompleteDefaultLimit = 10;
    private const int AutocompleteMaxLimit = 25;

    public static IEndpointRouteBuilder MapTagsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tags");
        group.MapGet("/", GetTags);
        group.MapGet("/{slug}", GetBySlug);
        group.MapGet("/{slug}/clips", GetClipsForTag);
        return app;
    }

    private static async Task<IResult> GetTags(
        string? prefix,
        int? limit,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit ?? AutocompleteDefaultLimit, 1, AutocompleteMaxLimit);

        // Normalize the prefix through the same canonicalization that ResolveAsync uses
        // so "Clu" and "clu" find the same rows. NormalizePrefix is lenient on length
        // (1+) so a single-character prefix still works for autocomplete.
        var normalized = TagNormalization.NormalizePrefix(prefix);

        var query = db.Tags.AsNoTracking().AsQueryable();
        if (normalized is not null)
        {
            // Escape LIKE metacharacters before appending '%'. Identical pattern to GamesEndpoints.
            var trimmed = normalized
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");
            var pattern = $"{trimmed}%";
            // Case-sensitive Like (not ILike): slug rows are normalized to lowercase at
            // insert time (TagsResolver) and the prefix is lowercased above (NormalizePrefix),
            // so both sides of the match are already canonical — ILike would be wasted work.
            query = query.Where(t => EF.Functions.Like(t.Slug, pattern, @"\"));
        }

        // Project clipCount in the same SELECT — single round trip, ordered by popularity
        // then alphabetical for stable cross-page ordering. The Where on Visibility/Status
        // matches the per-tag feed's filter so the count reflects what the user can actually
        // scroll through.
        var rows = await query
            .Select(t => new
            {
                t.Id,
                t.Slug,
                t.Name,
                ClipCount = db.Clips.Count(c =>
                    c.ClipTags.Any(ct => ct.TagId == t.Id)
                    && c.Visibility == "public"
                    && c.Status == "ready"),
            })
            .OrderByDescending(r => r.ClipCount)
            .ThenBy(r => r.Slug)
            .Take(clampedLimit)
            .ToListAsync(ct);

        var items = rows
            .Select(r => new TagSummary(r.Id, r.Slug, r.Name, r.ClipCount))
            .ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> GetBySlug(
        string slug,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var row = await db.Tags.AsNoTracking()
            .Where(t => t.Slug == slug)
            .Select(t => new
            {
                Tag = t,
                ClipCount = db.Clips.Count(c =>
                    c.ClipTags.Any(ct => ct.TagId == t.Id)
                    && c.Visibility == "public"
                    && c.Status == "ready"),
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? ProblemResults.NotFound("not_found")
            : Results.Ok(row.Tag.ToDetail(row.ClipCount));
    }

    private static async Task<IResult> GetClipsForTag(
        string slug,
        string? cursor,
        int? limit,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        // Distinguish "no such tag" (404) from "tag exists but has no clips" (200, empty page)
        // so the client picks the right empty state. Mirrors GamesEndpoints.GetClipsForGame.
        var tagId = await db.Tags.AsNoTracking()
            .Where(t => t.Slug == slug)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (tagId is null)
            return ProblemResults.NotFound("not_found");

        var resolvedTagId = tagId.Value;
        var baseQuery = db.Clips.AsNoTracking()
            .Where(c => c.ClipTags.Any(ct => ct.TagId == resolvedTagId)
                && c.Visibility == "public"
                && c.Status == "ready");

        var response = await ClipsReadEndpoints.BuildFeedPageAsync(
            baseQuery, cursor, limit, principal, db, storage, s3, ct);
        return Results.Ok(response);
    }
}
