using System.Text.RegularExpressions;
using FluentAssertions;
using GankedTV.Api.Clips;
using Xunit;

namespace GankedTV.Api.Tests.Clips;

public class ShareCodeGeneratorTests
{
    [Fact]
    public void Next_ReturnsEightCharsFromBase62Alphabet()
    {
        var code = ShareCodeGenerator.Next();

        code.Should().HaveLength(8);
        code.Should().MatchRegex(@"^[A-Za-z0-9]{8}$");
    }

    [Fact]
    public void Next_ProducesDistinctValuesAcrossManyCalls()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => ShareCodeGenerator.Next()).ToHashSet();

        // With 62^8 ≈ 218 trillion possible codes, 1000 draws should all be distinct
        codes.Should().HaveCount(1000);
    }
}
