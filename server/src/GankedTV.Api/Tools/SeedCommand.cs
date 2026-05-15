using GankedTV.Api.Auth.Passwords;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tools;

/// <summary>
/// Idempotent dev seed: creates one test user plus ten sample clips keyed by deterministic
/// ids, so repeated runs leave the DB in the same state. Invoked via
/// <c>dotnet run --project server/src/GankedTV.Api -- --seed</c>.
/// </summary>
public sealed class SeedCommand(
    GankedTvDbContext db,
    ILogger<SeedCommand> logger,
    TimeProvider clock,
    IHostEnvironment env,
    IPasswordHasher hasher)
{
    public const string FlagName = "--seed";

    public static readonly Guid SeedUserId = new("00000000-0000-0000-0000-00000000CAFE");
    public const string SeedUsername = "seeduser";
    public const string SeedUserEmail = $"{SeedUsername}@dev.local";
    // Documented in the README so contributors can hit /auth/login directly after `make seed`.
    public const string SeedUserPassword = "testpass123!";
    public const int SeedClipCount = 10;
    private const int GameRotationCount = 5;

    public static bool ShouldRun(string[] args) => args.Contains(FlagName);

    public async Task RunAsync(CancellationToken ct)
    {
        // Hard guard: seed is a dev-only tool. Running it against a production DB would
        // create a predictable test user with a predictable id, which is both a data-quality
        // and a security problem. Fail closed — ASPNETCORE_ENVIRONMENT must be Development.
        if (!env.IsDevelopment())
        {
            logger.LogError(
                "Seed refused: environment is {Env}, not Development. Set ASPNETCORE_ENVIRONMENT=Development to proceed.",
                env.EnvironmentName);
            return;
        }

        var now = clock.GetUtcNow();

        // Match by id first, then fall back to email/username. The columns have unique
        // indexes (idx_users_email, idx_users_username), so an id-only lookup followed by
        // an unconditional INSERT used to crash with 23505 when a non-canonical row
        // already occupied the seed's email or username — e.g. someone registering via
        // /auth/register with the documented seed credentials. Reuse that row instead.
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == SeedUserId || u.Email == SeedUserEmail || u.Username == SeedUsername,
            ct);
        if (user is null)
        {
            user = new User
            {
                Id = SeedUserId,
                Username = SeedUsername,
                Email = SeedUserEmail,
                Bio = "Seeded dev user.",
                PasswordHash = hasher.Hash(SeedUserPassword),
                PasswordAlgo = hasher.Algorithm,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seed: created user {Username}", user.Username);
        }
        else
        {
            if (user.Id != SeedUserId)
            {
                logger.LogWarning(
                    "Seed: existing user matches by email/username under id {Id} (expected {Expected}). Reusing existing row.",
                    user.Id, SeedUserId);
            }
            if (string.IsNullOrEmpty(user.PasswordHash))
            {
                // Migrate older seed runs that created the user before passwords existed.
                // Don't overwrite an existing password — a contributor may have rotated it via /auth/password.
                user.PasswordHash = hasher.Hash(SeedUserPassword);
                user.PasswordAlgo = hasher.Algorithm;
                user.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Seed: attached default password to existing user {Username}", user.Username);
            }
        }

        // Rotate seeded clips across the seeded games (Ids 1..GameRotationCount) so the
        // dev feed always shows clips with game tags rendered, without needing manual setup.
        var gameIds = await db.Games
            .OrderBy(g => g.Id)
            .Select(g => g.Id)
            .Take(GameRotationCount)
            .ToListAsync(ct);

        for (var i = 1; i <= SeedClipCount; i++)
        {
            var clipId = SeedClipId(i);
            var exists = await db.Clips.AnyAsync(c => c.Id == clipId, ct);
            if (exists) continue;

            db.Clips.Add(new Clip
            {
                Id = clipId,
                UserId = user.Id,
                GameId = gameIds.Count == 0 ? null : gameIds[(i - 1) % gameIds.Count],
                Title = SeedClipTitle(i),
                Description = $"Seeded sample clip #{i:D2}.",
                VideoKey = $"seed/clip-{i:D2}.mp4",
                ThumbnailKey = $"seed/clip-{i:D2}.jpg",
                Status = "ready",
                Visibility = "public",
                FileSizeBytes = 1_048_576L * i,
                DurationSecs = (short)(30 + i),
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i),
            });
        }

        var inserted = await db.SaveChangesAsync(ct);
        if (inserted > 0)
        {
            logger.LogInformation("Seed: inserted {Count} row(s).", inserted);
        }
        else
        {
            logger.LogInformation("Seed: already present, no changes.");
        }
    }

    // Deterministic ids so re-runs find the existing row via equality — no title-based lookup
    // (titles can legitimately collide with user-created clips, ids cannot).
    public static Guid SeedClipId(int i) =>
        new($"00000000-0000-0000-0000-0000000000{i:D2}");

    public static string SeedClipTitle(int i) => $"Seed Clip {i:D2}";
}
