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
    IHostEnvironment env)
{
    public const string FlagName = "--seed";

    public static readonly Guid SeedUserId = new("00000000-0000-0000-0000-00000000CAFE");
    public const string SeedUsername = "seeduser";
    public const int SeedClipCount = 10;

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

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == SeedUserId, ct);
        if (user is null)
        {
            user = new User
            {
                Id = SeedUserId,
                Username = SeedUsername,
                Email = $"{SeedUsername}@dev.local",
                Bio = "Seeded dev user.",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seed: created user {Username}", user.Username);
        }

        for (var i = 1; i <= SeedClipCount; i++)
        {
            var clipId = SeedClipId(i);
            var exists = await db.Clips.AnyAsync(c => c.Id == clipId, ct);
            if (exists) continue;

            db.Clips.Add(new Clip
            {
                Id = clipId,
                UserId = user.Id,
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
