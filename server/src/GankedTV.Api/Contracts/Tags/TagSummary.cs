namespace GankedTV.Api.Contracts.Tags;

/// <summary>
/// Compact tag projection embedded in clip DTOs and returned by the autocomplete endpoint.
/// <see cref="ClipCount"/> is populated only by <c>GET /tags?prefix=</c>; when nested under
/// a clip the count is <c>0</c> (clients only need it for the autocomplete dropdown).
/// </summary>
public sealed record TagSummary(int Id, string Slug, string Name, int ClipCount);
