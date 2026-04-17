namespace GankedTV.Api.Data.Entities;

public class Game
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? CoverUrl { get; set; }
    public int? IgdbId { get; set; }
}
