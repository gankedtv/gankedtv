using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.Tokens;

public sealed class RefreshTokenService : IRefreshTokenService
{
    // Treat "token revoked within the last N seconds" as a concurrent legit rotation rather than
    // a replay. Without this, two browser tabs racing to refresh would trip family revocation:
    // the winner revokes the row, the loser's lookup sees RevokedAt set, and the loser would
    // (wrongly) revoke the freshly-issued successor. A real replay typically arrives many
    // seconds-to-hours after the original was rotated.
    private static readonly TimeSpan ReplayGrace = TimeSpan.FromSeconds(30);

    private readonly GankedTvDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<RefreshTokenService> _logger;
    private readonly TimeSpan _ttl;

    public RefreshTokenService(
        GankedTvDbContext db,
        IOptions<RefreshTokenOptions> options,
        ILogger<RefreshTokenService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RefreshTokenService>.Instance;
        _ttl = TimeSpan.FromDays(options.Value.ExpiryDays);
    }

    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var raw = GenerateRaw();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            FamilyId = Guid.NewGuid(),
            ExpiresAt = _clock.GetUtcNow().Add(_ttl),
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    public async Task<RotateResult> RotateAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var now = _clock.GetUtcNow();

        // Atomic CAS: only one concurrent caller wins the revocation. Others see 0 rows
        // affected and we fall through to the replay-detection path below — a replayed /
        // race-duplicated rotation cannot succeed twice.
        var affected = await _db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        if (affected == 0)
        {
            await HandleRotateMissAsync(hash, now, ct);
            throw new InvalidRefreshTokenException("Refresh token is invalid, revoked, or expired.");
        }

        var row = await _db.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .SingleAsync(t => t.TokenHash == hash, ct);

        // Ban check before issuing the successor row: a banned client polling /auth/refresh
        // would otherwise insert one refresh_tokens row per call (the old token IS revoked
        // by the CAS above — that's intentional and security-positive — but minting a new
        // row that the BannedUserMiddleware will reject anyway is pure write amplification).
        // Revoking-without-replacing also breaks the refresh chain, which is the desired
        // outcome for a banned account.
        if (row.User.BannedAt is not null)
        {
            throw new BannedAccountException();
        }

        var newRaw = GenerateRaw();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = row.UserId,
            TokenHash = Hash(newRaw),
            FamilyId = row.FamilyId,
            ExpiresAt = now.Add(_ttl),
        });
        await _db.SaveChangesAsync(ct);

        return new RotateResult(row.User, newRaw);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var row = await _db.RefreshTokens
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null || row.RevokedAt is not null)
        {
            return;
        }
        row.RevokedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeFamilyAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var familyId = await _db.RefreshTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.FamilyId)
            .SingleOrDefaultAsync(ct);
        if (familyId is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        await _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public static string Hash(string raw) => OpaqueToken.Hash(raw);

    private static string GenerateRaw() => OpaqueToken.Generate();

    // The CAS update above failed. Three possible reasons: token unknown, token expired,
    // or token already revoked. Only the third is a strong theft signal — a previously-valid
    // token is being replayed after it was rotated/revoked. In that case revoke every live
    // token in the same family so a thief who rotated once cannot keep using their chain.
    // Unknown/expired hashes are silently ignored (caller throws InvalidRefreshTokenException).
    private async Task HandleRotateMissAsync(string hash, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await _db.RefreshTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.FamilyId, t.RevokedAt })
            .SingleOrDefaultAsync(ct);

        if (existing is null || existing.RevokedAt is null)
        {
            return;
        }

        if (now - existing.RevokedAt.Value < ReplayGrace)
        {
            // Concurrent legitimate rotation; the winner already revoked the row a moment ago.
            // Don't kill the user's freshly-issued successor.
            return;
        }

        var revoked = await _db.RefreshTokens
            .Where(t => t.FamilyId == existing.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        _logger.LogWarning(
            "Refresh token replay detected; revoked {Revoked} live token(s) in family {FamilyId}.",
            revoked, existing.FamilyId);
    }
}

public sealed class RefreshTokenOptions
{
    public int ExpiryDays { get; set; } = 30;
}
