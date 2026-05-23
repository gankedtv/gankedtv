namespace GankedTV.Api.Data.Entities;

// Append-only event row written on every dedup-passed POST /clips/{id}/view. Feeds the
// time-windowed trending query (views in last 24h/7d) — separate from the denormalised
// clips.view_count counter so we can answer "views since X" without scanning the clips table.
public class ClipView
{
    public long Id { get; set; }
    public Guid ClipId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Clip Clip { get; set; } = null!;
}
