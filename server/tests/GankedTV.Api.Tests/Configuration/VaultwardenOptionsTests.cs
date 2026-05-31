using FluentAssertions;
using GankedTV.Api.Configuration;

namespace GankedTV.Api.Tests.Configuration;

public class VaultwardenOptionsTests
{
    [Theory]
    [InlineData("https://vault", "api-key", true)]
    [InlineData(null, "api-key", false)]
    [InlineData("https://vault", null, false)]
    [InlineData("", "api-key", false)]
    [InlineData("   ", "api-key", false)]
    [InlineData("https://vault", "", false)]
    [InlineData("https://vault", "   ", false)]
    public void IsConfigured_RequiresBothBootstrapVars(string? apiUrl, string? apiKey, bool expected)
    {
        var opts = new VaultwardenOptions { ApiUrl = apiUrl, ApiKey = apiKey };
        opts.IsConfigured.Should().Be(expected);
    }

    [Theory]
    [InlineData("Production", "Secrets - PROD")]
    [InlineData("production", "Secrets - PROD")]
    [InlineData("Development", "Secrets - DEV")]
    [InlineData("Staging", "Secrets - DEV")]
    [InlineData("", "Secrets - DEV")]
    [InlineData(null, "Secrets - DEV")]
    public void ResolveCollection_DerivesFromEnvironment_WhenNoExplicitOverride(string? env, string expected)
    {
        VaultwardenOptions.ResolveCollection(null, env).Should().Be(expected);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData(null)]
    public void ResolveCollection_ExplicitOverride_AlwaysWins(string? env)
    {
        VaultwardenOptions.ResolveCollection("Secrets - CUSTOM", env).Should().Be("Secrets - CUSTOM");
    }

    [Fact]
    public void EffectiveCollection_UsesInstanceOverride_ThenFallsBackToEnvironment()
    {
        new VaultwardenOptions { Collection = "Secrets - CUSTOM" }
            .EffectiveCollection("Production").Should().Be("Secrets - CUSTOM");

        new VaultwardenOptions().EffectiveCollection("Production").Should().Be("Secrets - PROD");
        new VaultwardenOptions().EffectiveCollection("Development").Should().Be("Secrets - DEV");
    }
}
