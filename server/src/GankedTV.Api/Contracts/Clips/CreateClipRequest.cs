namespace GankedTV.Api.Contracts.Clips;

public sealed record CreateClipRequest(
    string? Title,
    string? Description,
    int? GameId,
    string? Visibility);
