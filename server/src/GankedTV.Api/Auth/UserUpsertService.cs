using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GankedTV.Api.Auth;

public sealed class UserUpsertService
{
    private const int MaxInsertRetries = 3;
    private const string UsernameIndex = "idx_users_username";

    private readonly GankedTvDbContext _db;
    private readonly TimeProvider _clock;

    public UserUpsertService(GankedTvDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<User> UpsertFromOAuthAsync(
        string providerName,
        OAuthUserInfo info,
        CancellationToken ct = default)
    {
        var existing = providerName switch
        {
            DiscordOAuthProvider.ProviderName =>
                await _db.Users.FirstOrDefaultAsync(u => u.DiscordId == info.ProviderUserId, ct),
            GoogleOAuthProvider.ProviderName =>
                await _db.Users.FirstOrDefaultAsync(u => u.GoogleId == info.ProviderUserId, ct),
            _ => throw new ArgumentException($"Unknown OAuth provider '{providerName}'.", nameof(providerName)),
        };

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(info.Email) && existing.Email != info.Email)
            {
                existing.Email = info.Email;
            }
            // Only populate avatar when the user doesn't already have one, so a user's
            // explicit PATCH /me choice isn't stomped on next sign-in.
            if (!string.IsNullOrWhiteSpace(info.AvatarUrl) && string.IsNullOrWhiteSpace(existing.AvatarUrl))
            {
                existing.AvatarUrl = info.AvatarUrl;
            }
            existing.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        // Link by email ONLY when the provider asserts the email is verified. Otherwise a
        // malicious user could sign up with a forged email address on one provider and
        // hijack the account of an existing user with the same email.
        if (info.EmailVerified && !string.IsNullOrWhiteSpace(info.Email))
        {
            var byEmail = await _db.Users.FirstOrDefaultAsync(u => u.Email == info.Email, ct);
            if (byEmail is not null)
            {
                SetProviderId(byEmail, providerName, info.ProviderUserId);
                if (!string.IsNullOrWhiteSpace(info.AvatarUrl) && byEmail.AvatarUrl is null)
                {
                    byEmail.AvatarUrl = info.AvatarUrl;
                }
                byEmail.UpdatedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                return byEmail;
            }
        }

        return await CreateNewUserWithRetryAsync(providerName, info, ct);
    }

    private async Task<User> CreateNewUserWithRetryAsync(
        string providerName,
        OAuthUserInfo info,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxInsertRetries; attempt++)
        {
            var now = _clock.GetUtcNow();
            var username = await UsernameGenerator.GenerateUniqueAsync(info.Username, _db.Users, ct);
            var user = new User
            {
                Username = username,
                Email = info.EmailVerified ? info.Email : null,
                AvatarUrl = info.AvatarUrl,
                CreatedAt = now,
                UpdatedAt = now,
            };
            SetProviderId(user, providerName, info.ProviderUserId);
            _db.Users.Add(user);
            try
            {
                await _db.SaveChangesAsync(ct);
                return user;
            }
            catch (DbUpdateException ex) when (IsUsernameUniqueViolation(ex))
            {
                // Lost the TOCTOU race against a concurrent signup that took the same slug —
                // detach and try again with a freshly generated candidate.
                _db.Entry(user).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            $"Failed to insert user after {MaxInsertRetries} attempts due to username collisions.");
    }

    private static bool IsUsernameUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(pg.ConstraintName, UsernameIndex, StringComparison.Ordinal);

    private static void SetProviderId(User user, string providerName, string providerUserId)
    {
        switch (providerName)
        {
            case DiscordOAuthProvider.ProviderName:
                user.DiscordId = providerUserId;
                break;
            case GoogleOAuthProvider.ProviderName:
                user.GoogleId = providerUserId;
                break;
            default:
                throw new ArgumentException($"Unknown OAuth provider '{providerName}'.", nameof(providerName));
        }
    }
}
