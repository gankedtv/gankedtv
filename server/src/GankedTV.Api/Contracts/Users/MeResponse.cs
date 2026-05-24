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
    bool HasPassword,
    // Authorization role — surfaced so the web client can gate admin UI (nav link, /admin
    // route). The JWT carries the same value as a claim; this field just saves the SPA from
    // having to decode the token.
    string Role);
