using System.Buffers.Text;
using System.Text;
using FluentAssertions;
using GankedTV.Api.Pagination;

namespace GankedTV.Api.Tests.Data;

public class TopFeedCursorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, 500, TimeSpan.Zero);

    [Fact]
    public void Build_Then_TryParse_RoundTripsAllFields()
    {
        var id = Guid.NewGuid();
        var token = TopFeedCursor.Build(42, 1337, T0, id);

        TopFeedCursor.TryParse(token, out var likes, out var views, out var createdAt, out var parsedId)
            .Should().BeTrue();
        likes.Should().Be(42);
        views.Should().Be(1337);
        createdAt.Should().Be(T0);
        parsedId.Should().Be(id);
    }

    [Fact]
    public void Build_ProducesUrlSafeToken_WithoutSpecialChars()
    {
        // Base64Url (alphabet A-Za-z0-9-_) so the cursor drops into a query string without
        // escaping — only the standard-Base64 chars +, /, = must be absent.
        var token = TopFeedCursor.Build(5, 5, T0, Guid.NewGuid());
        token.Should().NotContainAny("+", "/", "=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_NullOrBlank_ReturnsFalse(string? raw)
    {
        TopFeedCursor.TryParse(raw, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_CorruptBase64_ReturnsFalse()
    {
        // Not valid Base64Url — the decoder throws and TryParse swallows it as "no cursor".
        TopFeedCursor.TryParse("!!!not-base64!!!", out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_WrongFieldCount_ReturnsFalse()
    {
        // Well-formed Base64Url but only two of the four fields.
        var token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("5_10"));
        TopFeedCursor.TryParse(token, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_NonNumericCounts_ReturnsFalse()
    {
        var token = Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes($"x_10_{T0:O}_{Guid.NewGuid():D}"));
        TopFeedCursor.TryParse(token, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_BadGuid_ReturnsFalse()
    {
        var token = Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes($"5_10_{T0:O}_not-a-guid"));
        TopFeedCursor.TryParse(token, out _, out _, out _, out _).Should().BeFalse();
    }
}
