using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Providers;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Auth.Providers;

public class OAuthProviderRegistryTests
{
    private static IOAuthProvider FakeProvider(string name)
    {
        var p = Substitute.For<IOAuthProvider>();
        p.Name.Returns(name);
        return p;
    }

    private static OAuthProviderRegistry Build(OAuthOptions opts, params IOAuthProvider[] providers) =>
        new(providers, Options.Create(opts));

    [Fact]
    public void NothingConfigured_RegistryIsEmpty()
    {
        var registry = Build(
            new OAuthOptions { WebOrigin = "http://localhost:5173" },
            FakeProvider(DiscordOAuthProvider.ProviderName),
            FakeProvider(GoogleOAuthProvider.ProviderName));

        registry.ConfiguredProviderNames.Should().BeEmpty();
        registry.TryGet(DiscordOAuthProvider.ProviderName, out _).Should().BeFalse();
        registry.TryGet(GoogleOAuthProvider.ProviderName, out _).Should().BeFalse();
    }

    [Fact]
    public void OnlyDiscordConfigured_GoogleFilteredOut()
    {
        var opts = new OAuthOptions
        {
            WebOrigin = "http://localhost:5173",
            Discord = new OAuthProviderOptions
            {
                ClientId = "did",
                ClientSecret = "secret",
                RedirectUri = "http://localhost/callback",
            },
        };

        var registry = Build(opts,
            FakeProvider(DiscordOAuthProvider.ProviderName),
            FakeProvider(GoogleOAuthProvider.ProviderName));

        registry.ConfiguredProviderNames.Should().ContainSingle().Which.Should().Be(DiscordOAuthProvider.ProviderName);
        registry.TryGet(DiscordOAuthProvider.ProviderName, out _).Should().BeTrue();
        registry.TryGet(GoogleOAuthProvider.ProviderName, out _).Should().BeFalse();
    }

    [Fact]
    public void BothConfigured_BothReturned()
    {
        var configured = new OAuthProviderOptions
        {
            ClientId = "id",
            ClientSecret = "secret",
            RedirectUri = "http://localhost/cb",
        };
        var opts = new OAuthOptions
        {
            WebOrigin = "http://localhost:5173",
            Discord = configured,
            Google = new OAuthProviderOptions
            {
                ClientId = "gid",
                ClientSecret = "gsecret",
                RedirectUri = "http://localhost/gcb",
            },
        };

        var registry = Build(opts,
            FakeProvider(DiscordOAuthProvider.ProviderName),
            FakeProvider(GoogleOAuthProvider.ProviderName));

        registry.ConfiguredProviderNames.Should().BeEquivalentTo(
            new[] { DiscordOAuthProvider.ProviderName, GoogleOAuthProvider.ProviderName });
    }
}
