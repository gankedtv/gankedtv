namespace GankedTV.Api.Contracts.Games;

public sealed record GameListItem(int Id, string Name, string Slug, string Tag, string? CoverUrl);
