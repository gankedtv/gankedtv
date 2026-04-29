namespace GankedTV.Api.Data.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    // All tokens descended from a single login share one FamilyId, so when reuse of a revoked
    // token is detected (a strong theft signal) every live token in the family can be revoked
    // in a single bulk update. See RefreshTokenService.RotateAsync.
    public Guid FamilyId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
