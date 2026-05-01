using FluentAssertions;
using GankedTV.Api.Auth.Passwords;
using GankedTV.Api.Data;
using GankedTV.Api.Tests.TestSupport;
using GankedTV.Api.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GankedTV.Api.Tests.Tools;

[Collection("Postgres")]
public class SeedCommandTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;

    public SeedCommandTests(PostgresFixture fx) => _fx = fx;

    public async Task InitializeAsync() => await _fx.ResetAsync();
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
    public async Task FreshDb_CreatesOneUserAndTenClips()
    {
        await using var db = _fx.CreateContext();
        var seed = NewSeed(db);

        await seed.RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(1);
        (await verify.Clips.CountAsync()).Should().Be(SeedCommand.SeedClipCount);
        (await verify.Users.SingleAsync()).Username.Should().Be(SeedCommand.SeedUsername);
    }

    [Fact]
    public async Task RunTwice_IsIdempotent()
    {
        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db).RunAsync(CancellationToken.None);
        }

        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db).RunAsync(CancellationToken.None);
        }

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(1);
        (await verify.Clips.CountAsync()).Should().Be(SeedCommand.SeedClipCount);
    }

    [Fact]
    public async Task ClipIds_AreDeterministic()
    {
        await using var db = _fx.CreateContext();
        await NewSeed(db).RunAsync(CancellationToken.None);

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
            new Argon2idPasswordHasher());

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
        await using var db = _fx.CreateContext();
        await NewSeed(db).RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var user = await verify.Users.SingleAsync();
        user.PasswordHash.Should().NotBeNullOrEmpty();
        user.PasswordAlgo.Should().Be("argon2id");
        new Argon2idPasswordHasher().Verify(SeedCommand.SeedUserPassword, user.PasswordHash!).Should().BeTrue();
    }

    [Fact]
    public async Task RunTwice_DoesNotReplaceExistingPassword()
    {
        // Idempotency: a contributor who rotates the seed user's password via /auth/password
        // should not have it stomped on by a second `make seed`.
        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db).RunAsync(CancellationToken.None);
        }

        // Manually rotate the password directly in the DB.
        var hasher = new Argon2idPasswordHasher();
        var rotated = hasher.Hash("rotated-password-1234");
        await using (var db = _fx.CreateContext())
        {
            var user = await db.Users.SingleAsync();
            user.PasswordHash = rotated;
            await db.SaveChangesAsync();
        }

        // Second seed run should NOT overwrite the rotated password.
        await using (var db = _fx.CreateContext())
        {
            await NewSeed(db).RunAsync(CancellationToken.None);
        }

        await using var verify = _fx.CreateContext();
        var after = await verify.Users.SingleAsync();
        after.PasswordHash.Should().Be(rotated);
    }

    private SeedCommand NewSeed(GankedTvDbContext db) =>
        new(db, NullLogger<SeedCommand>.Instance, TimeProvider.System, new FakeHostEnvironment("Development"), new Argon2idPasswordHasher());

    private sealed class FakeHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "GankedTV.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
