namespace GankedTV.Api.Contracts.Leaderboards;

public sealed record GlobalLeaderboardResponse(
    string Window,
    IReadOnlyList<LeaderboardEntry> TopClips,
    IReadOnlyList<TopGameEntry> TopGames);
