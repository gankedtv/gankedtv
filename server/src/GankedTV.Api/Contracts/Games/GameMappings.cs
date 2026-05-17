using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.Games;

public static class GameMappings
{
    public static GameSummary ToGameSummary(this Game game) =>
        new(game.Id, game.Name, game.Slug, game.Tag);

    public static GameDetail ToDetail(this Game game, int clipCount) =>
        new(game.Id, game.Name, game.Slug, game.Tag, game.CoverUrl, clipCount);
}
