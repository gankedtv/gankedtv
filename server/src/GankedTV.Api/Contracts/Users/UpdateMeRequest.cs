namespace GankedTV.Api.Contracts.Users;

public sealed record UpdateMeRequest(string? Username, string? Bio, string? AvatarUrl);
