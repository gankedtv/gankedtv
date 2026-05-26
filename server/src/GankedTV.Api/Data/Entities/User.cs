namespace GankedTV.Api.Data.Entities;

public class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Bio { get; set; }
    public string? DiscordId { get; set; }
    public string? GoogleId { get; set; }
    public string? PasswordHash { get; set; }
    public string? PasswordAlgo { get; set; }

    // Tracks where the active avatar came from so OAuth login can safely refresh a
    // provider-sourced avatar (Discord CDN URLs rotate when the user changes their picture
    // and the old URL 404s) without stomping a user's own upload. Values: "upload",
    // "oauth:discord", "oauth:google", or null (legacy / no avatar).
    public string? AvatarSource { get; set; }

    // S3 key of the current uploaded avatar (set only when AvatarSource = "upload"). Lets us
    // delete the previous object on replace/clear so the bucket doesn't accumulate orphans.
    public string? AvatarObjectKey { get; set; }

    // The most recent avatar URL the OAuth provider presented at login, regardless of
    // whether we adopted it as the active AvatarUrl. Updated on every OAuth login.
    // Lets DELETE /auth/me/avatar restore the provider avatar instantly instead of waiting
    // for the next login.
    public string? OAuthAvatarUrl { get; set; }
    public string? OAuthAvatarSource { get; set; }

    public string? BannerUrl { get; set; }
    public string? BannerObjectKey { get; set; }

    // #RRGGBB. CHECK constraint enforces the format at the DB level.
    public string? AccentColor { get; set; }

    // Optional self-promoted handles, persisted as jsonb so we can add platforms later
    // without a schema migration. Handles are validated app-side (length + char regex).
    public SocialLinks? SocialLinks { get; set; }

    // Authorization role. Lower-cased string instead of an enum so it lands on the DB as a
    // simple text column (matches Clip.Status / Visibility) and the JWT claim can be the
    // raw value without an extra mapping layer.
    public string Role { get; set; } = UserRoles.User;

    // Moderation: when set, the user is banned. The login/refresh endpoints refuse to issue
    // new tokens, and BannedUserMiddleware rejects existing tokens on the next request.
    public DateTimeOffset? BannedAt { get; set; }
    public string? BannedReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<Clip> Clips { get; set; } = [];
    public ICollection<Like> Likes { get; set; } = [];
    public ICollection<Follow> Following { get; set; } = [];
    public ICollection<Follow> Followers { get; set; } = [];
    public ICollection<Comment> Comments { get; set; } = [];
}
