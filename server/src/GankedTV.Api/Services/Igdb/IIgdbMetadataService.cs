namespace GankedTV.Api.Services.Igdb;

/// <summary>One game's metadata as returned by IGDB (only the fields we mirror).</summary>
public sealed record IgdbGame(int Id, string Name, string? CoverImageId);

public interface IIgdbMetadataService
{
    /// <summary>
    /// The most-rated games that have cover art, descending. Returns at most <paramref name="count"/>.
    /// </summary>
    Task<IReadOnlyList<IgdbGame>> GetPopularGamesAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Downloads the JPEG bytes for an IGDB cover image id, or null if the image is missing.
    /// </summary>
    Task<byte[]?> DownloadCoverAsync(string imageId, CancellationToken ct = default);
}
