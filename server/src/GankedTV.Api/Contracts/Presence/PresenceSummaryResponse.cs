using GankedTV.Api.Contracts.Users;

namespace GankedTV.Api.Contracts.Presence;

// followsOnline is populated only for authenticated callers (empty otherwise) and capped at
// PresenceOptions.FollowsOnlineCap.
public sealed record PresenceSummaryResponse(int Online, IReadOnlyList<UserSummary> FollowsOnline);
