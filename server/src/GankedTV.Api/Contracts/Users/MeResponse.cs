namespace GankedTV.Api.Contracts.Users;

public sealed record MeResponse(
    Guid Id,
    string Username,
    string? Email,
    string? Bio,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    // True when the account has a password set (covers both password-registered and
    // OAuth-then-attached accounts). The web settings view keys off this to decide
    // whether to show "Set password" (first-time) or "Change password" (rotation) copy.
    bool HasPassword);
