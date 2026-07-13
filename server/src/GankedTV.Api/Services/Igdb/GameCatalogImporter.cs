using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Igdb;

/// <inheritdoc cref="IGameCatalogImporter"/>
public sealed class GameCatalogImporter(
    GankedTvDbContext db,
    IIgdbMetadataService igdb,
    IObjectStorageService storage,
    IOptions<S3Options> s3Options,
    IOptions<IgdbOptions> igdbOptions,
    ILogger<GameCatalogImporter> logger)
    : IGameCatalogImporter
{
    // Persist incrementally so a crash mid-run keeps prior progress (resumable).
    private const int SaveEvery = 25;

    // Route literals under /games/ that a game slug must never claim: /games/hot would
    // shadow the detail/clips routes for a game literally named "Hot". Hand-synced with
    // the route table in GamesEndpoints.MapGamesEndpoints.
    private static readonly string[] ReservedSlugs = ["hot"];

    public async Task<GameCatalogImportResult> RunAsync(CancellationToken ct = default)
    {
        var igdbOpts = igdbOptions.Value;
        if (!igdbOpts.IsConfigured)
        {
            logger.LogError(
                "IGDB import refused: IGDB_CLIENT_ID / IGDB_CLIENT_SECRET are not set. "
                + "Provide Twitch client credentials to backfill game metadata.");
            return GameCatalogImportResult.Skipped;
        }

        logger.LogInformation("Fetching up to {Count} popular games from IGDB…", igdbOpts.PopularImportCount);
        var games = await igdb.GetPopularGamesAsync(igdbOpts.PopularImportCount, ct);
        logger.LogInformation("IGDB returned {Count} games with cover art.", games.Count);

        return await ImportAsync(games, ct);
    }

    public async Task<GameCatalogImportResult> ImportAsync(IReadOnlyList<IgdbGame> games, CancellationToken ct = default)
    {
        var s3 = s3Options.Value;
        await storage.EnsureBucketsAsync(ct);

        // Reconcile by igdb_id, then by name so curated seeds (hand-picked slug/tag, e.g.
        // "cs2"/"CS2") get adopted in place rather than duplicated. Track only the rows this
        // batch can touch — the on-demand search import runs this on a request path. Slugs are
        // still read from the whole table (untracked) so generated slugs stay globally unique.
        var incomingIds = games.Select(g => g.Id).ToHashSet();
        var incomingNames = games.Select(g => g.Name.ToLowerInvariant()).ToHashSet();
        var existing = await db.Games
            .Where(g => (g.IgdbId != null && incomingIds.Contains(g.IgdbId.Value))
                || incomingNames.Contains(g.Name.ToLower()))
            .ToListAsync(ct);
        var byIgdbId = existing.Where(g => g.IgdbId is not null).ToDictionary(g => g.IgdbId!.Value);
        var byName = existing
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grp => grp.Key, grp => grp.First(), StringComparer.OrdinalIgnoreCase);
        var usedSlugs = await db.Games.AsNoTracking()
            .Select(g => g.Slug)
            .ToHashSetAsync(StringComparer.Ordinal, ct);

        var processed = 0;
        var created = 0;
        var coversMirrored = 0;
        var renamed = 0;
        foreach (var meta in games)
        {
            ct.ThrowIfCancellationRequested();

            if (byIgdbId.TryGetValue(meta.Id, out var game))
            {
                // already linked
            }
            else if (byName.TryGetValue(meta.Name, out game))
            {
                game.IgdbId = meta.Id; // adopt the curated seed row (IgdbManaged stays false)
                byIgdbId[meta.Id] = game;
                // Don't let a second IGDB game with the same name re-adopt this row — it should
                // become a new (slug-disambiguated) row instead.
                byName.Remove(meta.Name);
            }
            else
            {
                game = new Game
                {
                    Name = meta.Name,
                    Slug = UniqueSlug(meta.Name, meta.Id, usedSlugs),
                    Tag = GameNaming.Tag(meta.Name),
                    IgdbId = meta.Id,
                    IgdbManaged = true, // importer-owned ⇒ eligible for name refresh
                };
                db.Games.Add(game);
                byIgdbId[meta.Id] = game;
                created++;
            }

            // Display-name refresh: only for importer-managed rows, so curated seeds (incl.
            // adopted ones) keep their hand-picked name. Slug is intentionally left fixed.
            if (game.IgdbManaged && !string.Equals(game.Name, meta.Name, StringComparison.Ordinal))
            {
                logger.LogInformation("Renaming game {IgdbId}: '{Old}' → '{New}'.", meta.Id, game.Name, meta.Name);
                game.Name = meta.Name;
                renamed++;
            }

            // Cover refresh keyed on the IGDB image_id we last mirrored: download only when it
            // changed (covers drift; placeholders have null ⇒ always replaced). A single flaky
            // download shouldn't abort the run — log and continue; cover_image_id stays as-is so
            // a later run retries. The filter re-raises only *caller* cancellation: a download
            // that trips HttpClient's timeout also throws OperationCanceledException, and that's
            // a flaky download, not an abort.
            if (!string.Equals(game.CoverImageId, meta.CoverImageId, StringComparison.Ordinal))
            {
                try
                {
                    if (await MirrorCoverAsync(s3, game, meta, ct))
                    {
                        coversMirrored++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Failed to mirror cover for {Name} (igdb {IgdbId}); continuing.",
                        meta.Name, meta.Id);
                }
            }

            if (++processed % SaveEvery == 0)
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Import progress: {Processed}/{Total} games.", processed, games.Count);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Import complete: {Processed} games processed, {Created} created, {Covers} cover(s) mirrored, {Renamed} renamed.",
            processed, created, coversMirrored, renamed);
        return new GameCatalogImportResult(processed, created, coversMirrored, renamed);
    }

    /// <summary>
    /// Downloads the game's IGDB cover, mirrors it to the covers bucket under the slug key, and
    /// records the new cover_url + cover_image_id. Returns false (no-op) if IGDB has no image.
    /// </summary>
    private async Task<bool> MirrorCoverAsync(S3Options s3, Game game, IgdbGame meta, CancellationToken ct)
    {
        if (meta.CoverImageId is not { Length: > 0 } imageId)
        {
            return false;
        }

        var bytes = await igdb.DownloadCoverAsync(imageId, ct);
        if (bytes is null)
        {
            return false;
        }

        var key = GameCovers.BuildCoverKey(game.Slug);
        using var stream = new MemoryStream(bytes);
        await storage.PutObjectAsync(s3.GameCoversBucket, key, stream, GameCovers.ContentType, ct);
        game.CoverUrl = GameCovers.BuildCoverUrl(s3, key);
        game.CoverImageId = imageId;
        return true;
    }

    private static string UniqueSlug(string name, int igdbId, HashSet<string> used)
    {
        var baseSlug = GameNaming.Slug(name);
        if (!ReservedSlugs.Contains(baseSlug, StringComparer.Ordinal) && used.Add(baseSlug))
        {
            return baseSlug;
        }

        // Deterministic disambiguation. {slug}-{igdbId} is unique in practice, but loop
        // defensively so the returned slug is always one we actually reserved in `used` —
        // never a duplicate that would trip the unique index on insert.
        var candidate = $"{baseSlug}-{igdbId}";
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{baseSlug}-{igdbId}-{n}";
        }
        return candidate;
    }
}
