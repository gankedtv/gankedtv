namespace GankedTV.Api.Contracts.Users;

public sealed record MeResponse(
    Guid Id,
    string Username,
    string? Email,
    string? Bio,
    string? AvatarUrl,
    DateTimeOffset CreatedAt);
