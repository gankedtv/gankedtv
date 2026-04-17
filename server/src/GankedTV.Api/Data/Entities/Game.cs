namespace GankedTV.Api.Data.Entities;

public class Game
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? CoverUrl { get; set; }

    // External ID from IGDB (igdb.com) — lets us fetch cover art, release
    // date, genres, etc. from their API without owning that metadata.
    public int? IgdbId { get; set; }
}
