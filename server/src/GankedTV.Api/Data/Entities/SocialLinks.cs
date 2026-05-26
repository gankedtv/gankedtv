namespace GankedTV.Api.Data.Entities;

// Persisted as a jsonb column on users.social_links via EF's OwnsOne(...).ToJson() mapping
// in GankedTvDbContext. Storing nested as a single jsonb (rather than three sibling columns)
// lets us add new platforms without a migration; the trade-off is that each handle is
// validated app-side, not by per-column DB constraints. Nullable properties so a partial
// update can clear an individual platform (e.g. set Twitch only).
public sealed class SocialLinks
{
    public string? Twitch { get; set; }
    public string? YouTube { get; set; }
    public string? Twitter { get; set; }
}
