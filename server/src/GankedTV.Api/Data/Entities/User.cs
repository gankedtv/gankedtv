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
