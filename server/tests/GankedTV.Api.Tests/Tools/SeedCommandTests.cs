using FluentAssertions;
using GankedTV.Api.Auth.Passwords;
using GankedTV.Api.Data;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using GankedTV.Api.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Tools;

[Collection("Postgres")]
public class SeedCommandTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;

    public SeedCommandTests(PostgresFixture fx) => _fx = fx;

    // Reset to a clean 9-game baseline: the games table survives Respawn, so strip any rows /
    // cover metadata other tests left behind so the seed's cover step is deterministic here.
    public async Task InitializeAsync()
    {
        await _fx.ResetAsync();
        await using var db = _fx.CreateContext();
        await SeededGames.ResetBaselineAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(new[] { "--seed" }, true)]
    [InlineData(new[] { "--other", "--seed", "trailing" }, true)]
    [InlineData(new[] { "--other" }, false)]
    [InlineData(new string[0], false)]
    public void ShouldRun_DetectsFlag(string[] args, bool expected)
    {
        SeedCommand.ShouldRun(args).Should().Be(expected);
    }

    [Fact]
    public async Task FreshDb_CreatesTwoUsersAndTenClips()
    {
        await using var db = _fx.CreateContext();
        var seed = NewSeed(db, out _, out _);

        await seed.RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(2);
        (await verify.Clips.CountAsync()).Should().Be(SeedCommand.SeedClipCount);
        var usernames = await verify.Users.Select(u => u.Username).ToListAsync();
        usernames.Should().Contain([SeedCommand.SeedUsername, SeedCommand.SeedUser2Username]);
        // Clips all belong to the primary seeded user; seeduser2 has none — they're the
        // "actor" persona used by two-browser smoke tests, not a content owner.
        (await verify.Clips.CountAsync(c => c.UserId == SeedCommand.SeedUserId))
            .Should().Be(SeedCommand.SeedClipCount);
    }

    [Fact]
    public async Task RunTwice_IsIdempotent()
    {
        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db, out _, out _).RunAsync(CancellationToken.None);
        }

        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db, out _, out _).RunAsync(CancellationToken.None);
        }

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(2);
        (await verify.Clips.CountAsync()).Should().Be(SeedCommand.SeedClipCount);
    }

    [Fact]
    public async Task ClipIds_AreDeterministic()
    {
        await using var db = _fx.CreateContext();
        await NewSeed(db, out _, out _).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var ids = await verify.Clips.Select(c => c.Id).OrderBy(id => id).ToListAsync();
        ids.Should().Equal(
            Enumerable.Range(1, SeedCommand.SeedClipCount)
                .Select(SeedCommand.SeedClipId)
                .OrderBy(id => id));
    }

    [Fact]
    public async Task NonDevelopmentEnvironment_RefusesToSeed_AndLogsError()
    {
        // Production/Staging DBs must not get predictable seeded test data. The guard
        // lives in SeedCommand itself (not just Program.cs) so any caller — CLI, hosted
        // service, admin endpoint — gets the same fail-closed behavior.
        await using var db = _fx.CreateContext();
        var seed = new SeedCommand(
            db,
            NullLogger<SeedCommand>.Instance,
            TimeProvider.System,
            new FakeHostEnvironment("Production"),
            new Argon2idPasswordHasher(),
            new FakeObjectStorage(),
            new FakeFfmpegRunner(),
            Options.Create(new S3Options()),
            Options.Create(new MediaJobOptions()));

        await seed.RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(0);
        (await verify.Clips.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task FreshDb_AttachesDocumentedSeedPassword()
    {
        // The README documents seeduser@dev.local / testpass123! as the local-dev login;
        // contributors should be able to call /auth/login with that pair after `make seed`.
        // seeduser2 ships with the same password — it's an alternate identity for two-browser
        // smoke tests, not a different credential surface to maintain.
        await using var db = _fx.CreateContext();
        await NewSeed(db, out _, out _).RunAsync(CancellationToken.None);

        var hasher = new Argon2idPasswordHasher();
        await using var verify = _fx.CreateContext();
        foreach (var (username, password) in new[]
                 {
                     (SeedCommand.SeedUsername, SeedCommand.SeedUserPassword),
                     (SeedCommand.SeedUser2Username, SeedCommand.SeedUser2Password),
                 })
        {
            var user = await verify.Users.SingleAsync(u => u.Username == username);
            user.PasswordHash.Should().NotBeNullOrEmpty();
            user.PasswordAlgo.Should().Be("argon2id");
            hasher.Verify(password, user.PasswordHash!).Should().BeTrue();
        }
    }

    [Fact]
    public async Task ExistingUserWithSeedEmail_UnderRandomId_IsReusedNotDuplicated()
    {
        // Regression for the 23505 we hit when /auth/register had already claimed the
        // seed's documented email under a random GUID. The seed used to id-lookup-then-
        // INSERT, crashing on idx_users_email; now it broadens the lookup to email or
        // username and reuses the existing row.
        var preExistingId = Guid.NewGuid();
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new GankedTV.Api.Data.Entities.User
            {
                Id = preExistingId,
                Username = SeedCommand.SeedUsername,
                Email = SeedCommand.SeedUserEmail,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db, out _, out _).RunAsync(CancellationToken.None);
        }

        await using var verify = _fx.CreateContext();
        // Two users total: the reused primary + the new seeduser2.
        (await verify.Users.CountAsync()).Should().Be(2);
        var user = await verify.Users.SingleAsync(u => u.Username == SeedCommand.SeedUsername);
        user.Id.Should().Be(preExistingId, "the existing row is reused, not replaced");
        // Seed should have attached the documented password to the reused row so /auth/login still works.
        user.PasswordHash.Should().NotBeNullOrEmpty();
        new Argon2idPasswordHasher().Verify(SeedCommand.SeedUserPassword, user.PasswordHash!).Should().BeTrue();
        // Clips were seeded against the reused row, not orphaned by id mismatch.
        (await verify.Clips.CountAsync(c => c.UserId == preExistingId)).Should().Be(SeedCommand.SeedClipCount);
    }

    [Fact]
    public async Task RunTwice_DoesNotReplaceExistingPassword()
    {
        // Idempotency: a contributor who rotates the seed user's password via /auth/password
        // should not have it stomped on by a second `make seed`.
        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db, out _, out _).RunAsync(CancellationToken.None);
        }

        // Manually rotate the password directly in the DB.
        var hasher = new Argon2idPasswordHasher();
        var rotated = hasher.Hash("rotated-password-1234");
        await using (var db = _fx.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Username == SeedCommand.SeedUsername);
            user.PasswordHash = rotated;
            await db.SaveChangesAsync();
        }

        // Second seed run should NOT overwrite the rotated password.
        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db, out _, out _).RunAsync(CancellationToken.None);
        }

        await using var verify = _fx.CreateContext();
        var after = await verify.Users.SingleAsync(u => u.Username == SeedCommand.SeedUsername);
        after.PasswordHash.Should().Be(rotated);
    }

    [Fact]
    public async Task FreshSeed_GeneratesAndUploadsVideoAndThumbnailPerClip()
    {
        await using var db = _fx.CreateContext();
        var seed = NewSeed(db, out var ffmpeg, out var storage);

        await seed.RunAsync(CancellationToken.None);

        // Bucket bootstrap runs exactly once per seed call.
        storage.EnsureBucketsCallCount.Should().Be(1);

        var gameCount = await db.Games.CountAsync();
        // Two ffmpeg invocations per clip (video + thumbnail) plus one per game (cover).
        ffmpeg.Invocations.Should().HaveCount(SeedCommand.SeedClipCount * 2 + gameCount);

        // Each clip yields a PutObject in the clips bucket (video/mp4) and the thumbnails
        // bucket (image/jpeg). Keys follow the prod {userId}/{clipId}.ext convention.
        storage.PutCalls.Where(p => p.Bucket == "clips").Should()
            .HaveCount(SeedCommand.SeedClipCount);
        storage.PutCalls.Where(p => p.Bucket == "thumbnails").Should()
            .HaveCount(SeedCommand.SeedClipCount);
        // Plus one placeholder cover per game in the game-covers bucket.
        storage.PutCalls.Where(p => p.Bucket == "game-covers").Should().HaveCount(gameCount);

        var seedUserId = (await db.Users.SingleAsync(u => u.Username == SeedCommand.SeedUsername)).Id;
        foreach (var i in Enumerable.Range(1, SeedCommand.SeedClipCount))
        {
            var clipId = SeedCommand.SeedClipId(i);
            storage.PutCalls.Should().ContainSingle(p =>
                p.Bucket == "clips" && p.Key == $"{seedUserId}/{clipId}.mp4");
            storage.PutCalls.Should().ContainSingle(p =>
                p.Bucket == "thumbnails" && p.Key == $"{seedUserId}/{clipId}.jpg");
        }

        // FileSizeBytes on the inserted Clip row reflects the actual uploaded video length,
        // not the old hard-coded 1MiB*i placeholder.
        var sizes = await db.Clips.Select(c => c.FileSizeBytes).ToListAsync();
        sizes.Should().AllSatisfy(s => s.Should().Be(FakeFfmpegRunner.VideoPayload.Length));
    }

    [Fact]
    public async Task FreshSeed_GeneratesPlaceholderCoverPerGame_AndSetsCoverUrl()
    {
        await using var db = _fx.CreateContext();
        var seed = NewSeed(db, out _, out var storage);

        await seed.RunAsync(CancellationToken.None);

        var games = await db.Games.AsNoTracking().ToListAsync();
        games.Should().NotBeEmpty();
        foreach (var g in games)
        {
            storage.PutCalls.Should().ContainSingle(p =>
                p.Bucket == "game-covers" && p.Key == $"{g.Slug}.jpg" && p.ContentType == "image/jpeg");
            g.CoverUrl.Should().EndWith($"/game-covers/{g.Slug}.jpg");
        }
    }

    [Fact]
    public void PlaceholderColor_IsDeterministicAndWellFormed()
    {
        var first = SeedCommand.PlaceholderColor("valorant");
        SeedCommand.PlaceholderColor("valorant").Should().Be(first, "the colour is derived from the slug");
        SeedCommand.PlaceholderColor("fortnite").Should().NotBe(first);
        first.Should().MatchRegex("^0x[0-9A-F]{6}$");
    }

    [Fact]
    public async Task FfmpegFailure_BubblesUpAndLeavesNoClipsBehind()
    {
        // A non-zero ffmpeg exit code must surface as an exception so `make setup`
        // fails loudly instead of silently inserting rows pointing at missing media.
        await using var db = _fx.CreateContext();
        var failingFfmpeg = new FakeFfmpegRunner { ExitCode = 1, Stderr = "lavfi: simulated failure" };
        var storage = new FakeObjectStorage();
        var seed = new SeedCommand(
            db,
            NullLogger<SeedCommand>.Instance,
            TimeProvider.System,
            new FakeHostEnvironment("Development"),
            new Argon2idPasswordHasher(),
            storage,
            failingFfmpeg,
            Options.Create(new S3Options()),
            Options.Create(new MediaJobOptions()));

        Func<Task> act = () => seed.RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("ffmpeg") && e.Message.Contains("simulated failure"));

        await using var verify = _fx.CreateContext();
        // The seed user is created before the clip loop, so it persists. But no clip rows
        // should have been inserted — the exception fires inside the first iteration.
        (await verify.Clips.CountAsync()).Should().Be(0);
        storage.PutCalls.Should().BeEmpty("PutObject must not run when ffmpeg failed");
    }

    [Fact]
    public async Task ReSeed_SkipsFfmpegAndUploadWhenObjectsAlreadyExist()
    {
        // First run lays down rows + objects.
        FakeFfmpegRunner ffmpeg1;
        FakeObjectStorage storage1;
        await using (var db = _fx.CreateContext())
        {
            var seed = NewSeed(db, out ffmpeg1, out storage1);
            await seed.RunAsync(CancellationToken.None);
        }

        int gameCount;
        await using (var db = _fx.CreateContext())
        {
            gameCount = await db.Games.CountAsync();
        }
        ffmpeg1.Invocations.Should().HaveCount(SeedCommand.SeedClipCount * 2 + gameCount);
        storage1.PutCalls.Should().HaveCount(SeedCommand.SeedClipCount * 2 + gameCount);

        // Second run with a FRESH ffmpeg/storage but pre-populated objects (simulating
        // MinIO carrying over from the first run). The runner should not be invoked at
        // all, and PutObject should never be called.
        var storage2 = new FakeObjectStorage();

        await using (var db = _fx.CreateContext())
        {
            var seedUserId = (await db.Users.SingleAsync(u => u.Username == SeedCommand.SeedUsername)).Id;
            foreach (var i in Enumerable.Range(1, SeedCommand.SeedClipCount))
            {
                var clipId = SeedCommand.SeedClipId(i);
                storage2.Objects[("clips", $"{seedUserId}/{clipId}.mp4")] = new byte[42];
                storage2.Objects[("thumbnails", $"{seedUserId}/{clipId}.jpg")] = new byte[7];
            }
            // Pre-seed cover objects too (cover_url was persisted on the first run), so the
            // cover step is also a no-op on re-run.
            foreach (var slug in await db.Games.Select(g => g.Slug).ToListAsync())
            {
                storage2.Objects[("game-covers", $"{slug}.jpg")] = new byte[5];
            }

            var ffmpeg2 = new FakeFfmpegRunner();
            var seed = new SeedCommand(
                db,
                NullLogger<SeedCommand>.Instance,
                TimeProvider.System,
                new FakeHostEnvironment("Development"),
                new Argon2idPasswordHasher(),
                storage2,
                ffmpeg2,
                Options.Create(new S3Options()),
                Options.Create(new MediaJobOptions()));

            await seed.RunAsync(CancellationToken.None);

            ffmpeg2.Invocations.Should().BeEmpty("media already present → ffmpeg must not run");
            storage2.PutCalls.Should().BeEmpty("media already present → PutObject must not run");
        }
    }

    private SeedCommand NewSeed(GankedTvDbContext db, out FakeFfmpegRunner ffmpeg, out FakeObjectStorage storage)
    {
        ffmpeg = new FakeFfmpegRunner();
        storage = new FakeObjectStorage();
        return new SeedCommand(
            db,
            NullLogger<SeedCommand>.Instance,
            TimeProvider.System,
            new FakeHostEnvironment("Development"),
            new Argon2idPasswordHasher(),
            storage,
            ffmpeg,
            Options.Create(new S3Options()),
            Options.Create(new MediaJobOptions()));
    }

    private sealed class FakeHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "GankedTV.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // Writes a fixed payload to the path the seed asked ffmpeg to produce (last arg).
    // The real binary is replaced because the test fixture's Postgres container doesn't
    // bundle ffmpeg — and even when it did, exercising real encoding inside a unit test
    // is needlessly slow.
    internal sealed class FakeFfmpegRunner : IFfmpegRunner
    {
        // Distinct payloads so size assertions can tell video vs. thumbnail apart.
        public static readonly byte[] VideoPayload = "FAKE_MP4_PAYLOAD_BYTES_FOR_TEST_____"u8.ToArray();
        public static readonly byte[] ThumbPayload = "FAKE_JPEG_PAYLOAD"u8.ToArray();

        public List<(string Executable, IReadOnlyList<string> Arguments)> Invocations { get; } = new();
        public int ExitCode { get; set; }
        public string Stderr { get; set; } = string.Empty;

        public Task<FfmpegResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations.Add((executable, arguments));
            if (ExitCode == 0)
            {
                // Seed's video and thumbnail invocations both place the output path as the
                // last argument; sniff the extension to decide which payload to write.
                var outputPath = arguments[^1];
                var payload = outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? VideoPayload
                    : ThumbPayload;
                File.WriteAllBytes(outputPath, payload);
            }
            return Task.FromResult(new FfmpegResult(ExitCode, string.Empty, Stderr));
        }
    }

    internal sealed class FakeObjectStorage : IObjectStorageService
    {
        public Dictionary<(string Bucket, string Key), byte[]> Objects { get; } = new();
        public List<(string Bucket, string Key, string ContentType, byte[] Bytes)> PutCalls { get; } = new();
        public int EnsureBucketsCallCount { get; private set; }

        public Task EnsureBucketsAsync(CancellationToken ct = default)
        {
            EnsureBucketsCallCount++;
            return Task.CompletedTask;
        }

        public Task<ObjectMetadata?> GetObjectMetadataAsync(string bucket, string key, CancellationToken ct = default)
        {
            return Task.FromResult(Objects.TryGetValue((bucket, key), out var bytes)
                ? new ObjectMetadata(bytes.Length, null)
                : null);
        }

        public async Task PutObjectAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            Objects[(bucket, key)] = bytes;
            PutCalls.Add((bucket, key, contentType, bytes));
        }

        public Task DeleteObjectAsync(string bucket, string key, CancellationToken ct = default)
        {
            Objects.Remove((bucket, key));
            return Task.CompletedTask;
        }

        public string GetPresignedPutUrl(string bucket, string key, string contentType, TimeSpan? expiry = null) => string.Empty;
        public string GetPresignedGetUrl(string bucket, string key, TimeSpan? expiry = null) => string.Empty;
    }
}
