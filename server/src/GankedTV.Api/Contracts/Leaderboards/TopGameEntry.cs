using GankedTV.Api.Contracts.Games;

namespace GankedTV.Api.Contracts.Leaderboards;

public sealed record TopGameEntry(int Rank, int WindowLikes, int ClipCount, GameSummary Game, string? CoverUrl);
