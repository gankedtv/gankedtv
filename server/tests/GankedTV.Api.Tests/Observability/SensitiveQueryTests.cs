using FluentAssertions;
using GankedTV.Api.Observability;

namespace GankedTV.Api.Tests.Observability;

public class SensitiveQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_PassesThroughNullOrEmpty(string? input)
    {
        SensitiveQuery.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_DropsSensitiveParamsKeepsBenignOnes()
    {
        SensitiveQuery.Redact("?code=abc&state=xyz&page=2").Should().Be("?page=2");
    }

    [Fact]
    public void Redact_HandlesQueryWithoutLeadingQuestionMark()
    {
        SensitiveQuery.Redact("token=jwt&sort=week").Should().Be("sort=week");
    }

    [Fact]
    public void Redact_ReturnsEmptyWhenEveryParamIsSensitive()
    {
        SensitiveQuery.Redact("?code=abc&refresh=rt&access_token=at&id_token=it").Should().BeEmpty();
    }

    [Fact]
    public void Redact_MatchesKeysCaseInsensitively()
    {
        SensitiveQuery.Redact("?Code=abc&keep=1").Should().Be("?keep=1");
    }

    [Fact]
    public void Redact_KeepsBenignQueryUnchanged()
    {
        SensitiveQuery.Redact("?sort=week&page=2").Should().Be("?sort=week&page=2");
    }
}
