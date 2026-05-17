namespace GankedTV.Api.Contracts.Games;

public sealed record GameDetail(int Id, string Name, string Slug, string Tag, string? CoverUrl, int ClipCount);
