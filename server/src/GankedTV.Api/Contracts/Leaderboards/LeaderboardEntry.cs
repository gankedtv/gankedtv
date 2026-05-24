using GankedTV.Api.Contracts.Clips;

namespace GankedTV.Api.Contracts.Leaderboards;

public sealed record LeaderboardEntry(int Rank, int WindowLikes, ClipFeedItem Clip);
