using System.Net.Mail;
using GankedTV.Api.Auth.Passwords;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GankedTV.Api.Auth;

// Credential-flow counterpart to UserUpsertService (which handles the OAuth side).
// Splitting register/login/set-password into its own service keeps endpoint handlers
// thin and makes the policy/race-handling logic directly unit-testable.
public sealed class CredentialAuthService
{
    private const int MaxInsertRetries = 3;
    private const string UsernameIndex = "idx_users_username";
    private const string EmailIndex = "idx_users_email";

    // Sentinel hash for the no-user / no-password fallback in TryLoginAsync, used to
    // pay the Argon2 cost regardless of whether the email exists — without it, attackers
    // can probe email existence by timing the response.
    //
    // Cached statically (process-wide), not per-instance: CredentialAuthService is
    // registered scoped, so a per-instance constructor hash would burn ~190 ms of CPU on
    // every credential request, doubling Argon2 work on every login/register. The hasher
    // is a DI singleton, so a single sentinel survives the entire process lifetime.
    private static string? s_sentinelHash;

    private readonly GankedTvDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly TimeProvider _clock;

    public CredentialAuthService(
        GankedTvDbContext db,
        IPasswordHasher hasher,
        TimeProvider? clock = null)
    {
        _db = db;
        _hasher = hasher;
        _clock = clock ?? TimeProvider.System;
    }

    private string SentinelHash
    {
        get
        {
            // First reader wins. A race here just means two threads each compute one
            // ~190 ms hash and one writer's value is discarded — harmless: either hash
            // is a valid never-matches sentinel, and the race only fires once per process.
            var current = s_sentinelHash;
            if (current is not null) return current;
            var fresh = _hasher.Hash("sentinel-do-not-match-anything-12345!");
            return Interlocked.CompareExchange(ref s_sentinelHash, fresh, null) ?? fresh;
        }
    }

    public async Task<RegisterResult> TryRegisterAsync(
        string email,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var normalisedEmail = NormaliseEmail(email);
        if (normalisedEmail is null)
        {
            return RegisterResult.InvalidEmail;
        }

        var policy = PasswordPolicy.Validate(password, normalisedEmail, username);
        if (!policy.IsValid)
        {
            return RegisterResult.InvalidPassword(policy.Error!);
        }

        // Skip the optimistic pre-check on email uniqueness and let the unique-index catch
        // below be the canonical handler — keeps a single code path for both serial and
        // racing duplicates. The cost on duplicate-email registrations is one extra Argon2
        // hash (~190ms), which is acceptable: /auth/register is rate-limited per IP, and
        // duplicate-email submissions are rare in practice.
        var hash = _hasher.Hash(password);
        var algo = _hasher.Algorithm;

        for (var attempt = 0; attempt < MaxInsertRetries; attempt++)
        {
            var slug = await UsernameGenerator.GenerateUniqueAsync(username, _db.Users, ct);
            var now = _clock.GetUtcNow();
            var user = new User
            {
                Username = slug,
                Email = normalisedEmail,
                PasswordHash = hash,
                PasswordAlgo = algo,
                // The register endpoint validates AcceptedTerms=true before reaching here.
                TermsAcceptedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Users.Add(user);
            try
            {
                await _db.SaveChangesAsync(ct);
                return RegisterResult.Success(user);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex, EmailIndex))
            {
                // A concurrent registration committed the same email between our check and
                // this insert. Detach and bail with the same 409 the serial path produces.
                _db.Entry(user).State = EntityState.Detached;
                return RegisterResult.EmailTaken;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex, UsernameIndex))
            {
                // Lost the username race. Detach and try again with a fresh suffixed slug.
                _db.Entry(user).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            $"Failed to insert user after {MaxInsertRetries} username retries.");
    }

    public async Task<User?> TryLoginAsync(
        string email,
        string password,
        CancellationToken ct = default)
    {
        var normalisedEmail = NormaliseEmail(email);
        if (normalisedEmail is null)
        {
            // Still pay the Argon2 cost on bad-format emails so timing doesn't leak whether
            // the address is well-formed.
            _ = _hasher.Verify(password, SentinelHash);
            return null;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalisedEmail, ct);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            _ = _hasher.Verify(password, SentinelHash);
            return null;
        }

        if (!_hasher.Verify(password, user.PasswordHash))
        {
            return null;
        }

        return user;
    }

    public async Task<SetPasswordResult> SetPasswordAsync(
        Guid userId,
        string? currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            return SetPasswordResult.UserNotFound;
        }

        var policy = PasswordPolicy.Validate(newPassword, user.Email, user.Username);
        if (!policy.IsValid)
        {
            return SetPasswordResult.InvalidPassword(policy.Error!);
        }

        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            // Existing password on file → require and verify currentPassword.
            if (string.IsNullOrEmpty(currentPassword)
                || !_hasher.Verify(currentPassword, user.PasswordHash))
            {
                return SetPasswordResult.WrongCurrentPassword;
            }
        }
        // No existing password (OAuth-only user attaching one) → no currentPassword needed.

        user.PasswordHash = _hasher.Hash(newPassword);
        user.PasswordAlgo = _hasher.Algorithm;
        user.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return SetPasswordResult.Ok;
    }

    private static string? NormaliseEmail(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        // MailAddress is the same parser DataAnnotations [EmailAddress] uses.
        if (!MailAddress.TryCreate(raw.Trim(), out var addr))
        {
            return null;
        }
        return addr.Address.ToLowerInvariant();
    }

    private static bool IsUniqueViolation(DbUpdateException ex, string indexName) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(pg.ConstraintName, indexName, StringComparison.Ordinal);
}

public abstract record RegisterResult
{
    public static readonly RegisterResult InvalidEmail = new InvalidEmailResult();
    public static readonly RegisterResult EmailTaken = new EmailTakenResult();
    public static RegisterResult InvalidPassword(string error) => new InvalidPasswordResult(error);
    public static RegisterResult Success(User user) => new SuccessResult(user);

    public sealed record SuccessResult(User User) : RegisterResult;
    public sealed record InvalidEmailResult : RegisterResult;
    public sealed record EmailTakenResult : RegisterResult;
    public sealed record InvalidPasswordResult(string Error) : RegisterResult;
}

public abstract record SetPasswordResult
{
    public static readonly SetPasswordResult Ok = new OkResult();
    public static readonly SetPasswordResult UserNotFound = new UserNotFoundResult();
    public static readonly SetPasswordResult WrongCurrentPassword = new WrongCurrentPasswordResult();
    public static SetPasswordResult InvalidPassword(string error) => new InvalidPasswordResult(error);

    public sealed record OkResult : SetPasswordResult;
    public sealed record UserNotFoundResult : SetPasswordResult;
    public sealed record WrongCurrentPasswordResult : SetPasswordResult;
    public sealed record InvalidPasswordResult(string Error) : SetPasswordResult;
}
