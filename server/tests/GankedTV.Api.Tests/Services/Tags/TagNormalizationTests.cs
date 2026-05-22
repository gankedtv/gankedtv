using FluentAssertions;
using GankedTV.Api.Services.Tags;

namespace GankedTV.Api.Tests.Services.Tags;

public class TagNormalizationTests
{
    [Theory]
    [InlineData("clutch", "clutch")]
    [InlineData("Clutch", "clutch")]
    [InlineData("CLUTCH", "clutch")]
    [InlineData("  clutch  ", "clutch")]
    [InlineData("clutch-play", "clutch-play")]
    [InlineData("clutch play", "clutch-play")]
    [InlineData("clutch_play", "clutch-play")]
    [InlineData("clutch   play", "clutch-play")]
    [InlineData("clutch--play", "clutch-play")]
    [InlineData("--clutch--", "clutch")]
    [InlineData("c2", "c2")]
    [InlineData("éclair", "clair")] // accented chars stripped
    [InlineData("ace 🎯", "ace")]
    public void TryNormalize_ValidInputs_ProducesCanonicalSlug(string raw, string expected)
    {
        TagNormalization.TryNormalize(raw, out var slug).Should().BeTrue();
        slug.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("!!!")]
    public void TryNormalize_RejectsEmptyOrTooShort(string? raw)
    {
        TagNormalization.TryNormalize(raw, out var slug).Should().BeFalse();
        slug.Should().BeEmpty();
    }

    [Fact]
    public void TryNormalize_RejectsTooLong()
    {
        var raw = new string('a', TagNormalization.MaxLength + 1);
        TagNormalization.TryNormalize(raw, out _).Should().BeFalse();
    }

    [Fact]
    public void TryNormalize_AtMaxLength_Accepts()
    {
        var raw = new string('a', TagNormalization.MaxLength);
        TagNormalization.TryNormalize(raw, out var slug).Should().BeTrue();
        slug.Should().HaveLength(TagNormalization.MaxLength);
    }

    [Theory]
    [InlineData("c", "c")]
    [InlineData("Clu", "clu")]
    [InlineData("clutch-pl", "clutch-pl")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("!!!", null)]
    [InlineData("ABCdef-09", "abcdef-09")]
    public void NormalizePrefix_LowercasesAndStripsInvalid(string? raw, string? expected)
    {
        TagNormalization.NormalizePrefix(raw).Should().Be(expected);
    }
}
