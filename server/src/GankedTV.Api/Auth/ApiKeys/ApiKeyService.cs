using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Auth.ApiKeys;

// Personal access tokens for headless/desktop clients. A key is a high-entropy opaque secret
// hashed at rest (see OpaqueToken); it authenticates as its owning user with the user's full
// permissions — no scopes in v1.
public sealed class ApiKeyService
{
    // gtv_ prefix lets the auth scheme selector route "Authorization: Bearer gtv_…" to the
    // API-key handler (a JWT never starts with gtv_) and makes keys greppable for secret scanning.
    public const string KeyPrefix = "gtv_";

    // Length of the non-secret leading fragment stored for display (gtv_ + 8 chars).
    private const int DisplayPrefixLength = 12;

    // A single leaked JWT shouldn't be able to mint an unbounded key set. This is a cheaper,
    // clearer abuse bound than a rate limiter for a low-frequency, interactive-only operation.
    public const int MaxActiveKeysPerUser = 25;

    // Only refresh last_used_at when it's this stale — a key hammering the API would otherwise
    // trigger a row write per request (see the write-amplification note in RefreshTokenService).
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromSeconds(60);

    private readonly GankedTvDbContext _db;
    private readonly TimeProvider _clock;

    public ApiKeyService(GankedTvDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ApiKeyCreateResult> CreateAsync(
        Guid userId, string? name, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        // "Active" = usable right now: neither revoked nor expired. An already-expired key can't
        // authenticate, so it shouldn't count against the quota.
        //
        // Intentionally soft: the count and the insert below aren't serialized under a lock, so
        // two concurrent mints for the same user (e.g. approving two devices at once) could both
        // pass the check and briefly land a 26th key. The cap is an abuse bound, not a security
        // boundary, and the owner can revoke the extra — so this race is accepted rather than
        // paying for a per-mint SELECT FOR UPDATE / advisory lock on a rare, low-harm path.
        var active = await _db.ApiKeys
            .CountAsync(k => k.UserId == userId
                && k.RevokedAt == null
                && (k.ExpiresAt == null || k.ExpiresAt > now), ct);
        if (active >= MaxActiveKeysPerUser)
        {
            return ApiKeyCreateResult.Fail(ApiKeyCreateError.TooManyKeys);
        }

        var raw = OpaqueToken.Generate(KeyPrefix);
        var key = new ApiKey
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            KeyHash = OpaqueToken.Hash(raw),
            KeyPrefix = raw[..DisplayPrefixLength],
            ExpiresAt = expiresAt,
            CreatedAt = now,
        };
        _db.ApiKeys.Add(key);
        await _db.SaveChangesAsync(ct);

        return ApiKeyCreateResult.Ok(key, raw);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(Guid userId, CancellationToken ct = default) =>
        await _db.ApiKeys
            .AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);

    // Returns true when a live key owned by the caller was revoked; false when nothing matched
    // (unknown id, not the caller's, or already revoked) — the endpoint maps that to 404.
    public async Task<bool> RevokeAsync(Guid userId, Guid keyId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var affected = await _db.ApiKeys
            .Where(k => k.Id == keyId && k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), ct);
        return affected > 0;
    }

    // Resolves a raw key to its owning user, or null when the key is unknown, revoked, or
    // expired. On success it refreshes last_used_at (throttled) so the settings UI can show
    // recency without a write per request.
    public async Task<User?> AuthenticateAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rawKey) || !rawKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = OpaqueToken.Hash(rawKey);
        var now = _clock.GetUtcNow();

        var key = await _db.ApiKeys
            .AsNoTracking()
            .Include(k => k.User)
            .SingleOrDefaultAsync(k => k.KeyHash == hash, ct);

        if (key is null || key.RevokedAt is not null || (key.ExpiresAt is not null && key.ExpiresAt <= now))
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt.Value >= LastUsedThrottle)
        {
            await _db.ApiKeys
                .Where(k => k.Id == key.Id && (k.LastUsedAt == null || k.LastUsedAt < now - LastUsedThrottle))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        }

        return key.User;
    }
}

public enum ApiKeyCreateError
{
    TooManyKeys,
}

public readonly record struct ApiKeyCreateResult(ApiKey? Key, string? RawKey, ApiKeyCreateError? Error)
{
    public bool IsSuccess => Error is null;

    public static ApiKeyCreateResult Ok(ApiKey key, string rawKey) => new(key, rawKey, null);
    public static ApiKeyCreateResult Fail(ApiKeyCreateError error) => new(null, null, error);
}
