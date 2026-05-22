using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GankedTV.Api.Services.Tags;

public sealed class TagsResolver : ITagsResolver
{
    // Retry budget for the SELECT-then-INSERT race on a brand-new slug. One retry is
    // enough: after a concurrent transaction loses the race and a unique violation
    // surfaces, the next SELECT is guaranteed to see the winning row.
    private const int MaxInsertAttempts = 2;

    private readonly GankedTvDbContext _db;
    private readonly TimeProvider _clock;

    public TagsResolver(GankedTvDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // Resolver intentionally uses its own auto-commit semantics rather than running
    // inside the caller's transaction. Two trade-offs are baked into that choice:
    //   1. The race-retry below requires a live (non-aborted) connection to re-query
    //      after a unique violation. Inside a broken outer transaction Postgres rejects
    //      every subsequent statement until ROLLBACK, which would defeat the retry.
    //   2. If the caller's subsequent SaveChangesAsync fails, any tag rows this method
    //      just inserted remain. That's harmless — they're indistinguishable from rows
    //      a previous upload created and will be reused on the next get-or-create.
    public async Task<TagsResolution> ResolveAsync(IReadOnlyList<string> requested, CancellationToken ct)
    {
        if (requested.Count == 0)
        {
            return TagsResolution.Ok([]);
        }

        // Normalize first so the max-5 cap is applied AFTER dedupe — submitting
        // ["clutch", "Clutch", "CLUTCH"] should not 400 with too_many_tags.
        var slugToDisplay = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedSlugs = new List<string>(requested.Count);
        foreach (var raw in requested)
        {
            if (!TagNormalization.TryNormalize(raw, out var slug))
            {
                return TagsResolution.Fail(TagsResolveError.InvalidTag);
            }
            if (slugToDisplay.ContainsKey(slug)) continue;

            // Display name preserves the user's casing for first-seen tags. We strip
            // leading/trailing whitespace but otherwise keep the raw input within
            // the same length cap as the slug, so casing like "Clutch" survives.
            var display = (raw ?? string.Empty).Trim();
            if (display.Length > TagNormalization.MaxLength)
            {
                display = display[..TagNormalization.MaxLength];
            }
            slugToDisplay[slug] = display.Length == 0 ? slug : display;
            orderedSlugs.Add(slug);
        }

        if (orderedSlugs.Count > TagNormalization.MaxTagsPerClip)
        {
            return TagsResolution.Fail(TagsResolveError.TooManyTags);
        }

        var now = _clock.GetUtcNow();

        for (var attempt = 1; attempt <= MaxInsertAttempts; attempt++)
        {
            var existing = await _db.Tags
                .Where(t => orderedSlugs.Contains(t.Slug))
                .ToListAsync(ct);
            var bySlug = existing.ToDictionary(t => t.Slug, StringComparer.Ordinal);

            var newRows = new List<Tag>();
            foreach (var slug in orderedSlugs)
            {
                if (bySlug.ContainsKey(slug)) continue;
                var tag = new Tag
                {
                    Slug = slug,
                    Name = slugToDisplay[slug],
                    CreatedAt = now,
                };
                _db.Tags.Add(tag);
                newRows.Add(tag);
                bySlug[slug] = tag;
            }

            if (newRows.Count == 0)
            {
                return TagsResolution.Ok(orderedSlugs.Select(s => bySlug[s]).ToList());
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                return TagsResolution.Ok(orderedSlugs.Select(s => bySlug[s]).ToList());
            }
            catch (DbUpdateException ex) when (IsSlugUniqueViolation(ex) && attempt < MaxInsertAttempts)
            {
                // Another transaction inserted one of our slugs between our SELECT and our INSERT.
                // Detach the pending Added entries so the next attempt's SaveChanges doesn't
                // re-issue them; the next SELECT will return the winning rows.
                foreach (var row in newRows)
                {
                    _db.Entry(row).State = EntityState.Detached;
                }
            }
        }

        // Unreachable: the loop either returns on success or rethrows on the final attempt.
        throw new InvalidOperationException(
            $"TagsResolver.ResolveAsync exhausted {MaxInsertAttempts} attempts without resolving.");
    }

    private static bool IsSlugUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(pg.ConstraintName, "idx_tags_slug", StringComparison.Ordinal);

    public void SetClipTags(Clip clip, IReadOnlyList<Tag> resolved)
    {
        var targetIds = resolved.Select(t => t.Id).ToHashSet();
        var currentIds = clip.ClipTags.Select(ct => ct.TagId).ToHashSet();

        // Remove links that are no longer in the target set.
        var stale = clip.ClipTags.Where(ct => !targetIds.Contains(ct.TagId)).ToList();
        foreach (var ct in stale)
        {
            clip.ClipTags.Remove(ct);
            _db.ClipTags.Remove(ct);
        }

        // Add brand-new links.
        foreach (var tag in resolved)
        {
            if (currentIds.Contains(tag.Id)) continue;
            clip.ClipTags.Add(new ClipTag { ClipId = clip.Id, TagId = tag.Id, Tag = tag });
        }
    }
}
