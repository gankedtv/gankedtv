using Microsoft.Extensions.Caching.Memory;
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
    /// <summary>Returns true when at least one game was reconciled into the catalog.</summary>
    Task<bool> TryImportMatchesAsync(string term, CancellationToken ct = default);
}

/// <inheritdoc cref="IGameSearchImportService"/>
public sealed class GameSearchImportService(
    IIgdbMetadataService igdb,
    IGameCatalogImporter importer,
    IOptions<IgdbOptions> options,
    IMemoryCache memo,
    ILogger<GameSearchImportService> logger) : IGameSearchImportService
{
    // Small on purpose: bounds picker latency and per-miss cover mirroring.
    private const int SearchLimit = 5;
    private const int MinTermLength = 2;
    private static readonly TimeSpan MemoTtl = TimeSpan.FromMinutes(15);

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

        // Memoize per term — hit or miss — so repeated misses (typeahead keystrokes, abuse)
        // don't burn IGDB's 4 req/s budget.
        var memoKey = $"igdb:search:{normalized}";
        if (!memo.TryGetValue(memoKey, out _))
        {
            memo.Set(memoKey, true, MemoTtl);
            try
            {
                var matches = await igdb.SearchGamesAsync(normalized, SearchLimit, ct);
                if (matches.Count > 0)
                {
                    var result = await importer.ImportAsync(matches, ct);
                    return result.Processed > 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The picker must never fail because IGDB is down — the (possibly empty)
                // local result is still a valid answer.
                logger.LogWarning(ex, "On-demand IGDB search import failed for term '{Term}'.", term);
            }
        }

        return false;
    }
}
