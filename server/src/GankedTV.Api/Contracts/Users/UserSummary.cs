namespace GankedTV.Api.Contracts.Users;

public sealed record UserSummary(Guid Id, string Username, string? AvatarUrl);

public sealed record UserSummaryPage(IReadOnlyList<UserSummary> Items, string? NextCursor);
