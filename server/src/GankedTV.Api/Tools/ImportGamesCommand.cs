using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tools;

/// <summary>
/// Backfills the games catalog + cover art from IGDB. Pulls the most popular games, upserts
/// them into <c>games</c> keyed by <c>igdb_id</c>, mirrors each cover into the game-covers
/// bucket, and stores a stable public <c>cover_url</c>. Idempotent and resumable: re-runs skip
/// games that already have a cover object. Invoked via
/// <c>dotnet run --project server/src/GankedTV.Api -- --import-games</c>.
/// </summary>
public sealed class ImportGamesCommand(
    GankedTvDbContext db,
    IIgdbMetadataService igdb,
    IObjectStorageService storage,
    IOptions<S3Options> s3Options,
    IOptions<IgdbOptions> igdbOptions,
    ILogger<ImportGamesCommand> logger)
{
    public const string FlagName = "--import-games";

    // Persist incrementally so a crash mid-import keeps prior progress (resumable).
    private const int SaveEvery = 25;

    public static bool ShouldRun(string[] args) => args.Contains(FlagName);

    public async Task RunAsync(CancellationToken ct)
    {
        var igdbOpts = igdbOptions.Value;
        if (!igdbOpts.IsConfigured)
        {
            logger.LogError(
                "Import refused: IGDB_CLIENT_ID / IGDB_CLIENT_SECRET are not set. "
                + "Provide Twitch client credentials to backfill game metadata.");
            return;
        }

        var s3 = s3Options.Value;
        await storage.EnsureBucketsAsync(ct);

        logger.LogInformation("Fetching up to {Count} popular games from IGDB…", igdbOpts.PopularImportCount);
        var games = await igdb.GetPopularGamesAsync(igdbOpts.PopularImportCount, ct);
        logger.LogInformation("IGDB returned {Count} games with cover art.", games.Count);

        // Load existing catalog once. Reconcile by igdb_id, then by name so the original curated
        // seeds (whose Name matches IGDB's display name but whose slug/tag are hand-picked, e.g.
        // "cs2"/"CS2") get adopted in place — igdb_id + cover filled, slug/tag preserved — rather
        // than duplicated. usedSlugs keeps generated slugs unique against everything already present.
        var existing = await db.Games.ToListAsync(ct);
        var byIgdbId = existing.Where(g => g.IgdbId is not null).ToDictionary(g => g.IgdbId!.Value);
        var byName = existing
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grp => grp.Key, grp => grp.First(), StringComparer.OrdinalIgnoreCase);
        var usedSlugs = new HashSet<string>(existing.Select(g => g.Slug), StringComparer.Ordinal);

        var processed = 0;
        var coversWritten = 0;
        foreach (var meta in games)
        {
            ct.ThrowIfCancellationRequested();

            if (byIgdbId.TryGetValue(meta.Id, out var game))
            {
                // already linked
            }
            else if (byName.TryGetValue(meta.Name, out game))
            {
                game.IgdbId = meta.Id; // adopt the curated seed row
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
                };
                db.Games.Add(game);
                byIgdbId[meta.Id] = game;
            }

            // A single flaky cover download (transient network, a 5xx from the image CDN)
            // shouldn't abort the whole catalog import. Keep the game row — it's still
            // selectable in the upload picker — and let a later re-run retry the cover
            // (cover_url stays null ⇒ not skipped next time).
            try
            {
                if (await EnsureCoverAsync(s3, game, meta, ct))
                {
                    coversWritten++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to mirror cover for {Name} (igdb {IgdbId}); continuing.",
                    meta.Name, meta.Id);
            }

            if (++processed % SaveEvery == 0)
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Import progress: {Processed}/{Total} games.", processed, games.Count);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Import complete: {Processed} games processed, {Covers} cover(s) mirrored.",
            processed, coversWritten);
    }

    /// <summary>
    /// Ensures the game has a mirrored cover. Returns true if a cover was downloaded+uploaded
    /// this run. Skips work when cover_url is set and the object is already in the bucket.
    /// </summary>
    private async Task<bool> EnsureCoverAsync(S3Options s3, Game game, IgdbGame meta, CancellationToken ct)
    {
        if (meta.CoverImageId is not { Length: > 0 } imageId)
        {
            return false;
        }

        var key = GameCovers.BuildCoverKey(game.Slug);
        if (game.CoverUrl is { Length: > 0 }
            && await storage.GetObjectMetadataAsync(s3.GameCoversBucket, key, ct) is not null)
        {
            return false;
        }

        var bytes = await igdb.DownloadCoverAsync(imageId, ct);
        if (bytes is null)
        {
            return false;
        }

        using var stream = new MemoryStream(bytes);
        await storage.PutObjectAsync(s3.GameCoversBucket, key, stream, GameCovers.ContentType, ct);
        game.CoverUrl = GameCovers.BuildCoverUrl(s3, key);
        return true;
    }

    private static string UniqueSlug(string name, int igdbId, HashSet<string> used)
    {
        var baseSlug = GameNaming.Slug(name);
        if (used.Add(baseSlug))
        {
            return baseSlug;
        }

        // Deterministic disambiguation — IGDB ids are unique, so this always terminates.
        var candidate = $"{baseSlug}-{igdbId}";
        used.Add(candidate);
        return candidate;
    }
}
