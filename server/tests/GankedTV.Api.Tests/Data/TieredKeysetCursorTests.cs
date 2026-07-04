using FluentAssertions;
using GankedTV.Api.Pagination;

namespace GankedTV.Api.Tests.Data;

public class TieredKeysetCursorTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 4, 18, 23, 31, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_Then_TryParse_RoundTrips(int tier)
    {
        var token = TieredKeysetCursor.Build(tier, T, Id);

        var ok = TieredKeysetCursor.TryParse(token, out var parsedTier, out var parsedCreatedAt, out var parsedId);

        ok.Should().BeTrue();
        parsedTier.Should().Be(tier);
        parsedCreatedAt.Should().Be(T);
        parsedId.Should().Be(Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!not-base64!!!")]
    [InlineData("YWJj")] // valid Base64Url decoding to "abc" — no separators, wrong structure
    public void TryParse_NullEmptyOrCorrupt_ReturnsFalse_AndTierZero(string? raw)
    {
        var ok = TieredKeysetCursor.TryParse(raw, out var tier, out _, out _);

        ok.Should().BeFalse();
        tier.Should().Be(0);
    }

    [Fact]
    public void TryParse_PlainKeysetCursorToken_ReturnsFalse()
    {
        // A two-part (createdAt, id) token from Latest/Following must not parse as tiered —
        // it lacks the leading tier segment, so For You restarts from tier 0.
        var plain = KeysetCursor.Build(T, Id);

        var ok = TieredKeysetCursor.TryParse(plain, out var tier, out _, out _);

        ok.Should().BeFalse();
        tier.Should().Be(0);
    }

    [Fact]
    public void KeysetCursor_TryParse_RejectsTieredToken()
    {
        // Cross-source safety the other way: a tiered token fed to the Latest/Following decoder
        // fails (the id segment can't parse), so it falls back to no-cursor.
        var tiered = TieredKeysetCursor.Build(1, T, Id);

        KeysetCursor.TryParse(tiered, out _, out _).Should().BeFalse();
    }
}
