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
    public void Slugify_AccentedLatinChars_TransliteratesToAscii()
    {
        // NFD decomposes "ü" → "u" + combining diaeresis; we strip the combining mark.
        UsernameGenerator.Slugify("Jürgen!@#$Köhler").Should().Be("jurgenkohler");
    }

    [Fact]
    public void Slugify_NonLatinChars_StripsThem()
    {
        // Non-Latin scripts (e.g. Cyrillic, Han) don't decompose to ASCII; fall through to fallback.
        UsernameGenerator.Slugify("абв").Should().Be(UsernameGenerator.Fallback);
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
