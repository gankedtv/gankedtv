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
}

/// <summary>Counts from one import/sync pass, for logging and tests.</summary>
public sealed record GameCatalogImportResult(int Processed, int CoversMirrored, int Renamed)
{
    public static readonly GameCatalogImportResult Skipped = new(0, 0, 0);
}
