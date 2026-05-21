using GankedTV.Api.Services.Igdb;

namespace GankedTV.Api.Tools;

/// <summary>
/// One-shot CLI entry point for the IGDB catalog backfill. Delegates to
/// <see cref="IGameCatalogImporter"/> (the same code path the periodic
/// <see cref="IgdbSyncHostedService"/> uses). Invoked via
/// <c>dotnet run --project server/src/GankedTV.Api -- --import-games</c>.
/// </summary>
public sealed class ImportGamesCommand(IGameCatalogImporter importer)
{
    public const string FlagName = "--import-games";

    public static bool ShouldRun(string[] args) => args.Contains(FlagName);

    public Task RunAsync(CancellationToken ct) => importer.RunAsync(ct);
}
