using GankedTV.Api.Contracts.Users;

namespace GankedTV.Api.Contracts.Presence;

// followsOnline is populated only for authenticated callers (empty otherwise) and capped at
// PresenceOptions.FollowsOnlineCap; followsOnlineCount is the uncapped total so clients can
// render an honest "+N more" overflow.
public sealed record PresenceSummaryResponse(
    int Online,
    IReadOnlyList<UserSummary> FollowsOnline,
    int FollowsOnlineCount);
