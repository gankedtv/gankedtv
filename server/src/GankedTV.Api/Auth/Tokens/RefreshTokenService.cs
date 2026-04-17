using System.Security.Cryptography;
using System.Text;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Auth.Tokens;

public sealed class RefreshTokenService : IRefreshTokenService
{
    private const int TokenBytes = 32;

    private readonly GankedTvDbContext _db;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;

    public RefreshTokenService(
        GankedTvDbContext db,
        IOptions<RefreshTokenOptions> options,
        TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _ttl = TimeSpan.FromDays(options.Value.ExpiryDays);
    }

    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var raw = GenerateRaw();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
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
        // affected and throw, so a replayed / race-duplicated rotation cannot succeed twice.
        // TODO: detect reuse + revoke the whole token family per PLAN.md follow-up (#5 scope).
        var affected = await _db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        if (affected == 0)
        {
            throw new InvalidRefreshTokenException("Refresh token is invalid, revoked, or expired.");
        }

        var row = await _db.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .SingleAsync(t => t.TokenHash == hash, ct);

        var newRaw = GenerateRaw();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = row.UserId,
            TokenHash = Hash(newRaw),
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

    public static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateRaw()
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed class RefreshTokenOptions
{
    public int ExpiryDays { get; set; } = 30;
}
