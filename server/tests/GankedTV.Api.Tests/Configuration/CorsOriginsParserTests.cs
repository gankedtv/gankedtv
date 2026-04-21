using FluentAssertions;
using GankedTV.Api.Configuration;

namespace GankedTV.Api.Tests.Configuration;

public class CorsOriginsParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_ReturnsAlwaysIncludeOnly(string? raw)
    {
        CorsOriginsParser.Parse(raw, "http://web").Should().Equal("http://web");
    }

    [Fact]
    public void SingleOrigin_UnionedWithAlwaysInclude()
    {
        CorsOriginsParser.Parse("http://a.test", "http://web")
            .Should().Equal("http://a.test", "http://web");
    }

    [Fact]
    public void CommaSeparated_SplitsTrimsAndAppendsAlwaysInclude()
    {
        CorsOriginsParser.Parse("http://a.test, http://b.test ,http://c.test", "http://web")
            .Should().Equal("http://a.test", "http://b.test", "http://c.test", "http://web");
    }

    [Fact]
    public void EmptyEntries_Dropped()
    {
        CorsOriginsParser.Parse(",,http://a.test,,", "http://web")
            .Should().Equal("http://a.test", "http://web");
    }

    [Fact]
    public void WhitespaceOnlyEntries_FallBackToAlwaysInclude()
    {
        CorsOriginsParser.Parse(" , ,  ", "http://web")
            .Should().Equal("http://web");
    }

    [Fact]
    public void AlwaysIncludeAlreadyInList_NotDuplicated()
    {
        CorsOriginsParser.Parse("http://web,http://admin.test", "http://web")
            .Should().Equal("http://web", "http://admin.test");
    }

    [Fact]
    public void DuplicateEntries_Deduped()
    {
        CorsOriginsParser.Parse("http://a.test,http://a.test", "http://web")
            .Should().Equal("http://a.test", "http://web");
    }

    [Fact]
    public void WildcardInList_KeptAsLiteralOrigin()
    {
        // The parser doesn't interpret "*" — that's the caller's (AllowCredentials vs
        // SetIsOriginAllowed) concern. Here we just verify the literal survives parsing.
        CorsOriginsParser.Parse("*", "http://web")
            .Should().Equal("*", "http://web");
    }

    [Fact]
    public void EmptyAlwaysInclude_Throws()
    {
        var act = () => CorsOriginsParser.Parse("http://a.test", "");
        act.Should().Throw<ArgumentException>().WithParameterName("alwaysInclude");
    }
}
