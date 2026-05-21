using FluentAssertions;
using GankedTV.Api.Services.Igdb;

namespace GankedTV.Api.Tests.Services.Igdb;

public class GameNamingTests
{
    [Theory]
    [InlineData("Valorant", "valorant")]
    [InlineData("Counter-Strike 2", "counter-strike-2")]
    [InlineData("Tom Clancy's Rainbow Six® Siege", "tom-clancys-rainbow-six-siege")]
    [InlineData("  Spaced   Out  ", "spaced-out")]
    [InlineData("Pokémon", "pokemon")]
    [InlineData("S.T.A.L.K.E.R.", "s-t-a-l-k-e-r")]
    public void Slug_NormalizesNameToUrlSlug(string name, string expected)
    {
        GameNaming.Slug(name).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("™®©")]
    public void Slug_FallsBackForEmptyOrSymbolOnly(string? name)
    {
        GameNaming.Slug(name).Should().Be("game");
    }

    [Fact]
    public void Slug_CapsAtColumnLength()
    {
        var name = new string('a', 400);
        GameNaming.Slug(name).Length.Should().BeLessThanOrEqualTo(GameNaming.MaxSlugLength);
    }

    [Theory]
    [InlineData("League of Legends", "LL")]   // "of" dropped as a connector
    [InlineData("The Last of Us", "LU")]      // leading "The" and inner "of" both dropped
    [InlineData("Counter-Strike 2", "CS2")]
    [InlineData("Valorant", "VALORANT")]
    [InlineData("Dota 2", "D2")]
    public void Tag_DerivesShortUpperTag(string name, string expected)
    {
        GameNaming.Tag(name).Should().Be(expected);
    }

    [Fact]
    public void Tag_CapsAtSixteenChars()
    {
        var name = new string('a', 40);
        GameNaming.Tag(name).Length.Should().BeLessThanOrEqualTo(GameNaming.MaxTagLength);
    }

    [Fact]
    public void Tag_FallsBackForEmpty()
    {
        GameNaming.Tag("™").Should().Be("GAME");
    }
}
