namespace GankedTV.Api.Contracts.Tags;

/// <summary>
/// Header payload for the <c>/tag/:slug</c> page — the tag itself plus a live count
/// of public/ready clips, matching the count the paginated feed would walk through.
/// </summary>
public sealed record TagDetail(int Id, string Slug, string Name, int ClipCount);
