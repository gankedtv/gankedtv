namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// One game's metadata as returned by IGDB (only the fields we mirror).
/// <paramref name="AlternativeNames"/> carries IGDB's own alias list, which is what lets the
/// importer still recognise a game after upstream renames it (IGDB keeps the previous title
/// as an alias). Empty when the caller didn't request aliases.
/// </summary>
public sealed record IgdbGame(
    int Id,
    string Name,
    string? CoverImageId,
    IReadOnlyList<string>? AlternativeNames = null);

public interface IIgdbMetadataService
{
    /// <summary>
    /// The most-rated games that have cover art, descending. Returns at most <paramref name="count"/>.
    /// </summary>
    Task<IReadOnlyList<IgdbGame>> GetPopularGamesAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Full-catalog IGDB search for main games with cover art matching <paramref name="term"/>,
    /// in IGDB's own relevance order. Returns at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<IgdbGame>> SearchGamesAsync(string term, int limit, CancellationToken ct = default);

    /// <summary>
    /// Downloads the JPEG bytes for an IGDB cover image id, or null if the image is missing.
    /// </summary>
    Task<byte[]?> DownloadCoverAsync(string imageId, CancellationToken ct = default);
}
