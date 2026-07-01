namespace GankedTV.Api.Data.Entities;

public class ApiKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    // User-supplied label so the settings UI can tell keys apart ("rewynd desktop").
    public string? Name { get; set; }

    // SHA-256 of the raw key. The raw value is shown once at creation and never stored.
    public required string KeyHash { get; set; }

    // Non-secret leading fragment (e.g. "gtv_a1b2c3d4") so the list UI can identify a key
    // without ever holding the secret. Safe to display.
    public required string KeyPrefix { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    // Best-effort, throttled by ApiKeyService to avoid a write per authenticated request.
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public User User { get; set; } = null!;
}
