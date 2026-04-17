using FluentAssertions;
using GankedTV.Api.Auth;

namespace GankedTV.Api.Tests.Auth;

public class UsernameGeneratorSlugifyTests
{
    [Fact]
    public void Slugify_MixedCaseWithSpaces_ReturnsLowercaseHyphens()
    {
        UsernameGenerator.Slugify("Alice The Great").Should().Be("alice-the-great");
    }

    [Fact]
    public void Slugify_NonAsciiChars_StripsThem()
    {
        UsernameGenerator.Slugify("Jürgen!@#$Köhler").Should().Be("jrgenkhler");
    }

    [Fact]
    public void Slugify_LongerThan24_Truncates()
    {
        var slug = UsernameGenerator.Slugify("abcdefghijklmnopqrstuvwxyz1234567890");
        slug.Length.Should().Be(UsernameGenerator.MaxLength);
        slug.Should().Be("abcdefghijklmnopqrstuvwx");
    }

    [Fact]
    public void Slugify_EmptyAfterStripping_ReturnsFallback()
    {
        UsernameGenerator.Slugify("!!!").Should().Be(UsernameGenerator.Fallback);
        UsernameGenerator.Slugify("").Should().Be(UsernameGenerator.Fallback);
        UsernameGenerator.Slugify(null).Should().Be(UsernameGenerator.Fallback);
    }

    [Fact]
    public void Slugify_PreservesAllowedUnderscoresAndHyphens()
    {
        UsernameGenerator.Slugify("user_name-42").Should().Be("user_name-42");
    }
}
