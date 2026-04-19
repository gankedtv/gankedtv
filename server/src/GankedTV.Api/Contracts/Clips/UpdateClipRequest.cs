namespace GankedTV.Api.Contracts.Clips;

public sealed record UpdateClipRequest(
    string? Title,
    string? Description,
    int? GameId,
    string? Visibility);
