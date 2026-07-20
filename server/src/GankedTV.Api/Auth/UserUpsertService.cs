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
    private const string EmailIndex = "idx_users_email";

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
            // Only update the email when the incoming provider asserts verification, so a
            // compromised account whose `verified` was flipped can't overwrite a good email.
            if (info.EmailVerified && !string.IsNullOrWhiteSpace(info.Email) && existing.Email != info.Email)
            {
                existing.Email = info.Email;
            }
            RefreshAvatarFromProvider(existing, providerName, info);
            existing.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        // Link by email ONLY when the provider asserts the email is verified. Otherwise a
        // malicious user could sign up with a forged email address on one provider and
        // hijack the account of an existing user with the same email.
        var linked = await TryLinkByEmailAsync(providerName, info, ct);
        if (linked is not null)
        {
            return linked;
        }

        return await CreateNewUserWithRetryAsync(providerName, info, ct);
    }

    private async Task<User?> TryLinkByEmailAsync(
        string providerName,
        OAuthUserInfo info,
        CancellationToken ct)
    {
        if (!info.EmailVerified || string.IsNullOrWhiteSpace(info.Email))
        {
            return null;
        }
        var byEmail = await _db.Users.FirstOrDefaultAsync(u => u.Email == info.Email, ct);
        if (byEmail is null)
        {
            return null;
        }
        SetProviderId(byEmail, providerName, info.ProviderUserId);
        RefreshAvatarFromProvider(byEmail, providerName, info);
        byEmail.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return byEmail;
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
            var providerSource = $"oauth:{providerName}";
            var hasAvatar = !string.IsNullOrWhiteSpace(info.AvatarUrl);
            var user = new User
            {
                Username = username,
                Email = info.EmailVerified ? info.Email : null,
                AvatarUrl = info.AvatarUrl,
                AvatarSource = hasAvatar ? providerSource : null,
                OAuthAvatarUrl = hasAvatar ? info.AvatarUrl : null,
                OAuthAvatarSource = hasAvatar ? providerSource : null,
                // Sign-in-wrap: the login screen states that signing in constitutes
                // acceptance of the Terms, so first-time OAuth users are stamped here.
                TermsAcceptedAt = now,
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
            catch (DbUpdateException ex) when (IsEmailUniqueViolation(ex))
            {
                // A concurrent verified-email signup (different provider) committed between
                // our byEmail check and this insert. Canonical resolution: link to THAT user
                // instead of failing, mirroring the serial case.
                _db.Entry(user).State = EntityState.Detached;
                var linked = await TryLinkByEmailAsync(providerName, info, ct);
                if (linked is not null)
                {
                    return linked;
                }
                // Race winner isn't reachable by email lookup — fall through and retry the
                // insert; the next loop iteration will exit with the generic failure if we
                // keep losing.
            }
        }

        throw new InvalidOperationException(
            $"Failed to insert user after {MaxInsertRetries} attempts due to unique constraint violations.");
    }

    private static bool IsUsernameUniqueViolation(DbUpdateException ex) =>
        IsUniqueViolationOn(ex, UsernameIndex);

    private static bool IsEmailUniqueViolation(DbUpdateException ex) =>
        IsUniqueViolationOn(ex, EmailIndex);

    private static bool IsUniqueViolationOn(DbUpdateException ex, string indexName) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(pg.ConstraintName, indexName, StringComparison.Ordinal);

    // Avatar refresh policy for an existing user (called on every OAuth login, after we've
    // located the user via provider id or by linking through a verified email).
    //
    // Stash the provider's *current* avatar URL on the user regardless of whether we adopt it.
    // That lets DELETE /auth/me/avatar restore the provider picture immediately without waiting
    // for the next OAuth login.
    //
    // Adopt it as the *active* AvatarUrl only when:
    //   - AvatarSource is null (legacy row — first time we're classifying the source), OR
    //   - AvatarSource matches this same provider (the user is logging in with the provider
    //     that owns their avatar, and Discord's CDN hash may have rotated).
    //
    // A user-uploaded avatar (AvatarSource = "upload") is never overwritten. Neither is an
    // avatar sourced from a different provider — logging in with Google does not stomp the
    // user's Discord-sourced picture.
    private void RefreshAvatarFromProvider(User user, string providerName, OAuthUserInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.AvatarUrl))
        {
            return;
        }
        var providerSource = $"oauth:{providerName}";
        user.OAuthAvatarUrl = info.AvatarUrl;
        user.OAuthAvatarSource = providerSource;

        var sourceMatchesProvider = user.AvatarSource is null
            || string.Equals(user.AvatarSource, providerSource, StringComparison.Ordinal);
        if (sourceMatchesProvider)
        {
            // Always classify (so legacy NULL rows are stamped even when the URL is unchanged).
            // Only rewrite the URL when it actually differs to avoid spurious row churn.
            user.AvatarSource = providerSource;
            if (user.AvatarUrl != info.AvatarUrl)
            {
                user.AvatarUrl = info.AvatarUrl;
            }
        }
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
