using FluentAssertions;
using GankedTV.Api.Services.Caching;
using Xunit;

namespace GankedTV.Api.Tests.Services.Caching;

public class RedisOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("redis://localhost:6379", true)]
    public void IsConfigured_TracksUrlPresence(string? url, bool expected)
    {
        new RedisOptions { Url = url }.IsConfigured.Should().Be(expected);
    }

    [Fact]
    public void TryBuildConfiguration_ParsesHostAndPort()
    {
        var ok = new RedisOptions { Url = "redis://localhost:6380" }.TryBuildConfiguration(out var config);

        ok.Should().BeTrue();
        config.EndPoints.Should().ContainSingle();
        config.EndPoints[0].ToString().Should().Contain("localhost").And.Contain("6380");
        config.Ssl.Should().BeFalse();
        // AbortOnConnectFail must stay false so a down-at-boot Redis never hangs/throws startup.
        config.AbortOnConnectFail.Should().BeFalse();
    }

    [Fact]
    public void TryBuildConfiguration_DefaultsPortWhenOmitted()
    {
        new RedisOptions { Url = "redis://cache.internal" }.TryBuildConfiguration(out var config);

        config.EndPoints[0].ToString().Should().Contain("6379");
    }

    [Fact]
    public void TryBuildConfiguration_RedissEnablesTls()
    {
        var ok = new RedisOptions { Url = "rediss://secure-host:6380" }.TryBuildConfiguration(out var config);

        ok.Should().BeTrue();
        config.Ssl.Should().BeTrue();
    }

    [Fact]
    public void TryBuildConfiguration_ExtractsPasswordFromUserInfo()
    {
        // redis://:password@host and redis://user:password@host both carry the password second.
        new RedisOptions { Url = "redis://:s3cr3t@host:6379" }.TryBuildConfiguration(out var withColon);
        withColon.Password.Should().Be("s3cr3t");

        new RedisOptions { Url = "redis://aclu:p%40ss@host:6379" }.TryBuildConfiguration(out var withUser);
        withUser.Password.Should().Be("p@ss"); // %40 decoded
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://localhost:6379")] // wrong scheme
    public void TryBuildConfiguration_ReturnsFalse_ForMalformedOrWrongScheme(string? url)
    {
        // A bad URL must degrade to the in-process fallback, never crash startup.
        new RedisOptions { Url = url }.TryBuildConfiguration(out _).Should().BeFalse();
    }
}
