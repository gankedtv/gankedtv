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

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:5493")] // a worktree port
    [InlineData("https://localhost:8443")]
    [InlineData("http://LocalHost:5173")] // host comparison is case-insensitive per RFC 6454
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:9999")]
    [InlineData("http://[::1]:5173")]
    public void IsLocalhostOrigin_Accepts(string origin) =>
        CorsOriginsParser.IsLocalhostOrigin(origin).Should().BeTrue();

    [Theory]
    [InlineData("http://evil.test")]
    [InlineData("http://localhost.evil.test")] // suffix-based hijack attempt
    [InlineData("http://127.0.0.2")] // not loopback, despite the 127. prefix
    [InlineData("http://10.0.0.1")]
    [InlineData("ftp://localhost")] // wrong scheme
    [InlineData("file:///localhost")] // wrong scheme + format
    [InlineData("not a url")]
    [InlineData("")]
    public void IsLocalhostOrigin_Rejects(string origin) =>
        CorsOriginsParser.IsLocalhostOrigin(origin).Should().BeFalse();
}
