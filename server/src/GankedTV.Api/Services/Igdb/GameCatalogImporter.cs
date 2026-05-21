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

        var s3 = s3Options.Value;
        await storage.EnsureBucketsAsync(ct);

        logger.LogInformation("Fetching up to {Count} popular games from IGDB…", igdbOpts.PopularImportCount);
        var games = await igdb.GetPopularGamesAsync(igdbOpts.PopularImportCount, ct);
        logger.LogInformation("IGDB returned {Count} games with cover art.", games.Count);

        // Load existing catalog once. Reconcile by igdb_id, then by name so the original curated
        // seeds (whose Name matches IGDB's display name but whose slug/tag are hand-picked, e.g.
        // "cs2"/"CS2") get adopted in place rather than duplicated. usedSlugs keeps generated
        // slugs unique against everything already present.
        var existing = await db.Games.ToListAsync(ct);
        var byIgdbId = existing.Where(g => g.IgdbId is not null).ToDictionary(g => g.IgdbId!.Value);
        var byName = existing
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grp => grp.Key, grp => grp.First(), StringComparer.OrdinalIgnoreCase);
        var usedSlugs = new HashSet<string>(existing.Select(g => g.Slug), StringComparer.Ordinal);

        var processed = 0;
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
            // a later run retries.
            if (!string.Equals(game.CoverImageId, meta.CoverImageId, StringComparison.Ordinal))
            {
                try
                {
                    if (await MirrorCoverAsync(s3, game, meta, ct))
                    {
                        coversMirrored++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
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
            "Import complete: {Processed} games processed, {Covers} cover(s) mirrored, {Renamed} renamed.",
            processed, coversMirrored, renamed);
        return new GameCatalogImportResult(processed, coversMirrored, renamed);
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
        if (used.Add(baseSlug))
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
