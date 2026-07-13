using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// On-demand catalog backfill for the game picker: when a local search misses, look the term
/// up on IGDB and reconcile any matches into the catalog so a retried local query finds them.
/// The popularity import only carries the top N games by rating count, so long-tail titles
/// are otherwise unfindable. No-op without IGDB credentials.
/// </summary>
public interface IGameSearchImportService
{
    /// <summary>
    /// Returns true when the catalog changed in a way the caller's local search can now find —
    /// a new row, or a renamed importer-managed one. Adopting or re-covering a row it already
    /// had doesn't count: the retry would return the same miss.
    /// </summary>
    Task<bool> TryImportMatchesAsync(string term, CancellationToken ct = default);
}

/// <inheritdoc cref="IGameSearchImportService"/>
public sealed class GameSearchImportService(
    IIgdbMetadataService igdb,
    IGameCatalogImporter importer,
    IOptions<IgdbOptions> options,
    GameSearchMemo memo,
    ILogger<GameSearchImportService> logger) : IGameSearchImportService
{
    // Small on purpose: bounds picker latency and per-miss cover mirroring.
    private const int SearchLimit = 5;
    private const int MinTermLength = 3;
    private static readonly TimeSpan MemoTtl = TimeSpan.FromMinutes(15);

    // A failed lookup only cools the term down instead of memoizing it for the full TTL —
    // IGDB can recover seconds later and the term must not stay blackholed until MemoTtl.
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    // Whole-import budget. Every HTTP leg (token, search, cover downloads, S3 puts) carries its
    // own 30s timeout and IGDB calls are serialized behind a throttle, so without an overall
    // deadline one picker keystroke could hold a request (and its DB connection) for minutes.
    private static readonly TimeSpan ImportBudget = TimeSpan.FromSeconds(8);

    public async Task<bool> TryImportMatchesAsync(string term, CancellationToken ct = default)
    {
        if (!options.Value.IsConfigured)
        {
            return false;
        }

        var normalized = term.Trim().ToLowerInvariant();
        if (normalized.Length < MinTermLength)
        {
            return false;
        }

        if (memo.IsMemoized(normalized))
        {
            return false;
        }

        memo.Remember(normalized, MemoTtl);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ImportBudget);
        try
        {
            var matches = await igdb.SearchGamesAsync(normalized, SearchLimit, budget.Token);
            var relevant = matches.Where(m => Matches(m.Name, normalized)).ToList();
            if (relevant.Count > 0)
            {
                var result = await importer.ImportAsync(relevant, budget.Token);
                return result.Created > 0 || result.Renamed > 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away. Don't memoize a term IGDB never answered for.
            memo.Forget(normalized);
            throw;
        }
        catch (Exception ex)
        {
            // The picker must never fail because IGDB is down or slow — the (possibly empty)
            // local result is still a valid answer. Note this also catches the timeout paths
            // (HttpClient's own, and ImportBudget above), which surface as a
            // TaskCanceledException with the caller's token still alive.
            memo.Remember(normalized, FailureCooldown);
            logger.LogWarning(ex, "On-demand IGDB search import failed for term '{Term}'.", term);
        }

        return false;
    }

    // IGDB's `search` is fuzzy, so a typeahead prefix pulls in titles that don't contain the term
    // at all. Importing those would mint permanent catalog rows (and mirrored covers) that the
    // caller's retried query can't even find. Mirror that query's predicate — name or slug
    // contains the term — so nothing is written the retry won't return.
    private static bool Matches(string name, string normalized) =>
        name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
        || GameNaming.Slug(name).Contains(normalized, StringComparison.Ordinal);
}
