namespace GankedTV.Api.Data.Entities;

public class Tag
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ClipTag> ClipTags { get; set; } = [];
}
