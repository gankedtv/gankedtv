namespace GankedTV.Api.Services.Presence;

public sealed class PresenceOptions
{
    public bool Enabled { get; set; } = true;

    // A viewer counts as "online" if last seen within this window. Must exceed the client's poll
    // interval so a client that just polled stays counted until its next poll lands.
    public int WindowSeconds { get; set; } = 120;

    // Upper bound on the follows-online set returned to an authenticated caller (the avatar stack).
    public int FollowsOnlineCap { get; set; } = 20;
}
