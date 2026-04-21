using FluentAssertions;
using GankedTV.Api.Tests.TestSupport;
using GankedTV.Api.Tools;
using Microsoft.EntityFrameworkCore;
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
        var seed = new SeedCommand(db, NullLogger<SeedCommand>.Instance, TimeProvider.System);

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
            var seed = new SeedCommand(db, NullLogger<SeedCommand>.Instance, TimeProvider.System);
            await seed.RunAsync(CancellationToken.None);
        }

        await using (var db = _fx.CreateContext())
        {
            var seed = new SeedCommand(db, NullLogger<SeedCommand>.Instance, TimeProvider.System);
            await seed.RunAsync(CancellationToken.None);
        }

        await using var verify = _fx.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(1);
        (await verify.Clips.CountAsync()).Should().Be(SeedCommand.SeedClipCount);
    }

    [Fact]
    public async Task ClipIds_AreDeterministic()
    {
        await using var db = _fx.CreateContext();
        var seed = new SeedCommand(db, NullLogger<SeedCommand>.Instance, TimeProvider.System);
        await seed.RunAsync(CancellationToken.None);

        await using var verify = _fx.CreateContext();
        var ids = await verify.Clips.Select(c => c.Id).OrderBy(id => id).ToListAsync();
        ids.Should().Equal(
            Enumerable.Range(1, SeedCommand.SeedClipCount)
                .Select(SeedCommand.SeedClipId)
                .OrderBy(id => id));
    }
}
