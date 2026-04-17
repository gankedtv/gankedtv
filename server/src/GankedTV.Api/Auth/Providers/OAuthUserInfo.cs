namespace GankedTV.Api.Auth.Providers;

// EmailVerified defaults to `false` so new providers / test callers opt IN to the
// auto-link-by-email path rather than silently inheriting trust.
public sealed record OAuthUserInfo(
    string ProviderUserId,
    string? Email,
    string? Username,
    string? AvatarUrl,
    bool EmailVerified = false);
