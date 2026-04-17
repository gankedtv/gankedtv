using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Auth;

public sealed class UserUpsertService
{
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
            if (!string.IsNullOrWhiteSpace(info.AvatarUrl) && existing.AvatarUrl != info.AvatarUrl)
            {
                existing.AvatarUrl = info.AvatarUrl;
            }
            existing.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        // Link by email when the email is already known to us (e.g. user had Discord, now signs in with Google).
        if (!string.IsNullOrWhiteSpace(info.Email))
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

        var now = _clock.GetUtcNow();
        var username = await UsernameGenerator.GenerateUniqueAsync(info.Username, _db.Users, ct);
        var user = new User
        {
            Username = username,
            Email = info.Email,
            AvatarUrl = info.AvatarUrl,
            CreatedAt = now,
            UpdatedAt = now,
        };
        SetProviderId(user, providerName, info.ProviderUserId);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

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
