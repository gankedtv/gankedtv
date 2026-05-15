using GankedTV.Api.Auth.Passwords;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tools;

/// <summary>
/// Idempotent dev seed: creates one test user plus ten sample clips keyed by deterministic
/// ids, generates real playable media (mp4 + jpg) for each via ffmpeg, and uploads it to
/// MinIO. Repeated runs leave the DB + bucket state untouched. Invoked via
/// <c>dotnet run --project server/src/GankedTV.Api -- --seed</c>.
/// </summary>
public sealed class SeedCommand(
    GankedTvDbContext db,
    ILogger<SeedCommand> logger,
    TimeProvider clock,
    IHostEnvironment env,
    IPasswordHasher hasher,
    IObjectStorageService storage,
    IFfmpegRunner ffmpeg,
    IOptions<S3Options> s3Options,
    IOptions<MediaJobOptions> mediaOptions)
{
    public const string FlagName = "--seed";

    public static readonly Guid SeedUserId = new("00000000-0000-0000-0000-00000000CAFE");
    public const string SeedUsername = "seeduser";
    public const string SeedUserEmail = $"{SeedUsername}@dev.local";
    // Documented in the README so contributors can hit /auth/login directly after `make seed`.
    public const string SeedUserPassword = "testpass123!";
    public const int SeedClipCount = 10;
    private const int GameRotationCount = 5;

    // Synthetic clip dimensions / duration — kept small so the seed runs fast and stays
    // well under MinIO's default request size. Reflected in the Clip row's Width/Height
    // /DurationSecs columns so the row matches the uploaded bytes.
    private const int SyntheticWidth = 640;
    private const int SyntheticHeight = 360;
    private const int SyntheticDurationSecs = 5;

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

        // Ensure buckets exist before any PutObject. Seed runs via the `--seed` short-circuit
        // which doesn't start hosted services, so BucketBootstrapHostedService hasn't run.
        // EnsureBucketsAsync is a no-op when the buckets already exist.
        await storage.EnsureBucketsAsync(ct);

        // Rotate seeded clips across the seeded games (Ids 1..GameRotationCount) so the
        // dev feed always shows clips with game tags rendered, without needing manual setup.
        var gameIds = await db.Games
            .OrderBy(g => g.Id)
            .Select(g => g.Id)
            .Take(GameRotationCount)
            .ToListAsync(ct);

        var s3 = s3Options.Value;

        for (var i = 1; i <= SeedClipCount; i++)
        {
            var clipId = SeedClipId(i);
            // Mirror the prod upload key convention from ClipUploadService.BuildVideoKey /
            // BuildThumbnailKey so seeded objects sit alongside real ones in MinIO.
            var videoKey = $"{user.Id}/{clipId}.mp4";
            var thumbnailKey = $"{user.Id}/{clipId}.jpg";

            // Generate + upload media before the row exists. A partially-failed prior run
            // can leave the DB row present but the objects missing (or vice versa); this
            // path repairs either side.
            var videoSize = await EnsureSeedClipMediaAsync(s3, videoKey, thumbnailKey, ct);

            var existing = await db.Clips.FirstOrDefaultAsync(c => c.Id == clipId, ct);
            if (existing is not null) continue;

            db.Clips.Add(new Clip
            {
                Id = clipId,
                UserId = user.Id,
                GameId = gameIds.Count == 0 ? null : gameIds[(i - 1) % gameIds.Count],
                Title = SeedClipTitle(i),
                Description = $"Seeded sample clip #{i:D2}.",
                VideoKey = videoKey,
                ThumbnailKey = thumbnailKey,
                Status = "ready",
                Visibility = "public",
                FileSizeBytes = videoSize,
                DurationSecs = SyntheticDurationSecs,
                Width = SyntheticWidth,
                Height = SyntheticHeight,
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

    /// <summary>
    /// Ensures both the video and the thumbnail object exist in MinIO. Returns the video's
    /// byte size (for the Clip row's FileSizeBytes column). When both objects already exist
    /// we skip the ffmpeg invocation entirely — idempotent on re-runs and on partial-failure
    /// recovery.
    /// </summary>
    private async Task<long> EnsureSeedClipMediaAsync(
        S3Options s3,
        string videoKey,
        string thumbnailKey,
        CancellationToken ct)
    {
        var videoMeta = await storage.GetObjectMetadataAsync(s3.ClipsBucket, videoKey, ct);
        var thumbMeta = await storage.GetObjectMetadataAsync(s3.ThumbnailsBucket, thumbnailKey, ct);
        if (videoMeta is not null && thumbMeta is not null)
        {
            return videoMeta.SizeBytes;
        }

        var media = mediaOptions.Value;
        var tempDir = Path.Combine(Path.GetTempPath(), $"gankedtv-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var videoPath = Path.Combine(tempDir, "clip.mp4");
        var thumbPath = Path.Combine(tempDir, "thumb.jpg");

        try
        {
            long videoSize;
            // Generate the video only when it's actually missing from MinIO. The thumbnail
            // ffmpeg pass needs a local mp4 to extract a frame from, so if both are missing
            // we have to produce the video locally anyway — but if only the thumbnail is
            // gone we'd still need to re-download the existing object. Pragmatic choice:
            // when the video is present but the thumbnail isn't, we DO regenerate the
            // video locally rather than pull it back from MinIO — this stays a single
            // ffmpeg run instead of an S3 GET + ffmpeg, and the seed flow is dev-only
            // so a few seconds of extra encode work isn't worth a download path.
            if (videoMeta is null || thumbMeta is null)
            {
                // Synthetic clip: testsrc2 video + 440Hz tone, 5 seconds, libx264 ultrafast.
                // Output path is the LAST argument — the test fakes parse args[^1] to capture
                // the destination, so don't reorder without updating the test seam.
                var videoArgs = new[]
                {
                    "-y",
                    "-f", "lavfi", "-i", $"testsrc2=duration={SyntheticDurationSecs}:size={SyntheticWidth}x{SyntheticHeight}:rate=30",
                    "-f", "lavfi", "-i", $"sine=frequency=440:duration={SyntheticDurationSecs}",
                    "-c:v", "libx264",
                    "-preset", "ultrafast",
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac",
                    "-shortest",
                    videoPath,
                };
                var videoResult = await ffmpeg.RunAsync(media.FfmpegPath, videoArgs, media.ProcessTimeout, ct);
                if (videoResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"ffmpeg video generation failed (exit {videoResult.ExitCode}). stderr: {videoResult.Stderr}");
                }
            }

            if (videoMeta is null)
            {
                await using var videoStream = File.OpenRead(videoPath);
                videoSize = videoStream.Length;
                await storage.PutObjectAsync(s3.ClipsBucket, videoKey, videoStream, "video/mp4", ct);
                logger.LogInformation("Seed: uploaded video {Key} ({Bytes} bytes)", videoKey, videoSize);
            }
            else
            {
                videoSize = videoMeta.SizeBytes;
            }

            if (thumbMeta is null)
            {
                // Single-frame thumbnail at 2s. -q:v 5 is mid-quality JPEG; the output is ~10 KB.
                var thumbArgs = new[]
                {
                    "-y",
                    "-ss", "2",
                    "-i", videoPath,
                    "-frames:v", "1",
                    "-q:v", "5",
                    thumbPath,
                };
                var thumbResult = await ffmpeg.RunAsync(media.FfmpegPath, thumbArgs, media.ProcessTimeout, ct);
                if (thumbResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"ffmpeg thumbnail generation failed (exit {thumbResult.ExitCode}). stderr: {thumbResult.Stderr}");
                }

                await using var thumbStream = File.OpenRead(thumbPath);
                await storage.PutObjectAsync(s3.ThumbnailsBucket, thumbnailKey, thumbStream, "image/jpeg", ct);
                logger.LogInformation("Seed: uploaded thumbnail {Key} ({Bytes} bytes)", thumbnailKey, thumbStream.Length);
            }

            return videoSize;
        }
        finally
        {
            // Best-effort cleanup. The temp dir is unique per call (Guid.NewGuid) so a
            // failure to delete only leaks a few KB; we don't want a cleanup error to
            // mask the real exception from ffmpeg / PutObject.
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
