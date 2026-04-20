using FluentAssertions;
using GankedTV.Api.Auth;

namespace GankedTV.Api.Tests.Auth;

public class OAuthOptionsTests
{
    [Fact]
    public void AnyProviderConfigured_FalseWhenNeitherProviderHasCredentials()
    {
        // The ValidateOnStart predicate in Program.cs short-circuits on this: when no provider
        // is configured, the 32-byte StateSecret requirement is relaxed so the app can boot
        // without OAuth configured at all. If this flipped to true-by-default, a fresh clone
        // without env vars would fail to start.
        var opts = new OAuthOptions { WebOrigin = "http://localhost:5173" };

        opts.AnyProviderConfigured.Should().BeFalse();
    }

    [Fact]
    public void AnyProviderConfigured_TrueWhenOnlyDiscordConfigured()
    {
        var opts = new OAuthOptions
        {
            WebOrigin = "http://localhost:5173",
            Discord = new OAuthProviderOptions
            {
                ClientId = "c",
                ClientSecret = "s",
                RedirectUri = "http://cb",
            },
        };

        opts.AnyProviderConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "s", "r")]
    [InlineData("c", "", "r")]
    [InlineData("c", "s", "")]
    [InlineData("   ", "s", "r")]
    public void IsConfigured_RequiresAllThreeValues(string clientId, string secret, string redirect)
    {
        var opts = new OAuthProviderOptions
        {
            ClientId = clientId,
            ClientSecret = secret,
            RedirectUri = redirect,
        };

        opts.IsConfigured.Should().BeFalse();
    }
}
