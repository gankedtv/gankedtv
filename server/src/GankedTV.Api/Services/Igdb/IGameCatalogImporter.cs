namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// Pulls popular games from IGDB and reconciles them into the local catalog: creates new rows,
/// adopts curated seeds by name, mirrors cover art, and re-downloads a cover only when IGDB's
/// image changed. The single code path behind both the <c>--import-games</c> command (one-shot
/// backfill) and the periodic <see cref="IgdbSyncHostedService"/> (re-sync).
/// </summary>
public interface IGameCatalogImporter
{
    Task<GameCatalogImportResult> RunAsync(CancellationToken ct = default);

    /// <summary>
    /// Reconciles an explicit set of IGDB games into the catalog using the same
    /// adopt/insert/cover-mirror rules as <see cref="RunAsync"/>. Used by the on-demand
    /// search import, which resolves its own candidates instead of the popularity window.
    /// </summary>
    Task<GameCatalogImportResult> ImportAsync(IReadOnlyList<IgdbGame> games, CancellationToken ct = default);
}

/// <summary>
/// Counts from one import/sync pass, for logging and tests. <paramref name="Processed"/> counts
/// every input game (including ones already reconciled); <paramref name="Created"/> counts only
/// the rows this pass added, which is what the on-demand search import keys its retry off.
/// </summary>
public sealed record GameCatalogImportResult(int Processed, int Created, int CoversMirrored, int Renamed)
{
    public static readonly GameCatalogImportResult Skipped = new(0, 0, 0, 0);
}
