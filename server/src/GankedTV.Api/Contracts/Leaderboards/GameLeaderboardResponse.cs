using GankedTV.Api.Contracts.Games;

namespace GankedTV.Api.Contracts.Leaderboards;

public sealed record GameLeaderboardResponse(
    string Window,
    GameSummary Game,
    IReadOnlyList<LeaderboardEntry> Entries);
