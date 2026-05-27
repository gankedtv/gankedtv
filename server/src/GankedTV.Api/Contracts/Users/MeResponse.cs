using System.Text.Json.Serialization;

namespace GankedTV.Api.Contracts.Users;

public sealed record MeResponse(
    Guid Id,
    string Username,
    string? Email,
    string? Bio,
    string? AvatarUrl,
    // Where the active avatar came from — "upload", "oauth:discord", "oauth:google", or null.
    // Surfaced on the SELF endpoint only (not the public profile) so the edit modal can show
    // "Using your Discord avatar" copy and conditionally show the "Reset" button.
    string? AvatarSource,
    // The provider's most recent avatar URL, regardless of whether it was adopted. Lets the
    // edit modal show the "Reset to OAuth avatar" affordance only when there's something to
    // reset to. Explicit JsonPropertyName because the default camelCase policy emits
    // `oAuthAvatarUrl` from consecutive capitals.
    [property: JsonPropertyName("oauthAvatarUrl")]
    string? OAuthAvatarUrl,
    string? BannerUrl,
    string? AccentColor,
    SocialLinksDto? SocialLinks,
    DateTimeOffset CreatedAt,
    // True when the account has a password set (covers both password-registered and
    // OAuth-then-attached accounts). The web settings view keys off this to decide
    // whether to show "Set password" (first-time) or "Change password" (rotation) copy.
    bool HasPassword,
    // Authorization role — surfaced so the web client can gate admin UI (nav link, /admin
    // route). The JWT carries the same value as a claim; this field just saves the SPA from
    // having to decode the token.
    string Role);
