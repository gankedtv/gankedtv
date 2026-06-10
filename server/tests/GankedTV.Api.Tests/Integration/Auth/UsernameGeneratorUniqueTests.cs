using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;

namespace GankedTV.Api.Tests.Integration.Auth;

[Collection("PostgresAuth")]
public class UsernameGeneratorUniqueTests
{
    private readonly PostgresFixture _fx;

    public UsernameGeneratorUniqueTests(PostgresFixture fx) => _fx = fx;

    private async Task SeedUsernamesAsync(params string[] usernames)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        foreach (var name in usernames)
        {
            db.Users.Add(new User
            {
                Username = name,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GenerateUniqueAsync_NoCollision_ReturnsSlug()
    {
        await _fx.ResetAsync();

        await using var db = _fx.CreateContext();
        var result = await UsernameGenerator.GenerateUniqueAsync("Alice", db.Users);

        result.Should().Be("alice");
    }

    [Fact]
    public async Task GenerateUniqueAsync_Collision_AppendsFourHexSuffix()
    {
        await _fx.ResetAsync();
        await SeedUsernamesAsync("alice");

        await using var db = _fx.CreateContext();
        var result = await UsernameGenerator.GenerateUniqueAsync("Alice", db.Users);

        result.Should().MatchRegex("^alice-[0-9a-f]{4}$");
    }

    [Fact]
    public async Task GenerateUniqueAsync_LongBaseWithCollision_TruncatesBeforeSuffix()
    {
        await _fx.ResetAsync();
        var longName = new string('a', UsernameGenerator.MaxLength);
        await SeedUsernamesAsync(longName);

        await using var db = _fx.CreateContext();
        var result = await UsernameGenerator.GenerateUniqueAsync(longName, db.Users);

        result.Length.Should().Be(UsernameGenerator.MaxLength);
        result.Should().MatchRegex("^a+-[0-9a-f]{4}$");
    }
}
