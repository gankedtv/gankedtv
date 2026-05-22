namespace GankedTV.Api.Data.Entities;

public class ClipTag
{
    public Guid ClipId { get; set; }
    public int TagId { get; set; }

    public Clip Clip { get; set; } = null!;
    public Tag Tag { get; set; } = null!;
}
