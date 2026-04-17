namespace GankedTV.Api.Auth.Providers;

public sealed record OAuthUserInfo(
    string ProviderUserId,
    string? Email,
    string? Username,
    string? AvatarUrl,
    bool EmailVerified = true);
