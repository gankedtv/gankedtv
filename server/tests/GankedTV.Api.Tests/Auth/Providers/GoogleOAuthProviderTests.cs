using System.Net;
using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Auth.Providers;

public class GoogleOAuthProviderTests
{
    private static (GoogleOAuthProvider provider, TestHttpMessageHandler handler) BuildProvider()
    {
        var handler = new TestHttpMessageHandler();
        var options = Options.Create(new OAuthOptions
        {
            StateSecret = "state-secret-32-bytes-minimum-xxxxxx",
            WebOrigin = "http://localhost:5173",
            Google = new OAuthProviderOptions
            {
                ClientId = "google-client-id",
                ClientSecret = "google-client-secret",
                RedirectUri = "http://localhost:5000/auth/google/callback",
            },
        });
        var factory = FakeHttpClientFactory.Create(handler);
        return (new GoogleOAuthProvider(factory, options), handler);
    }

    [Fact]
    public void BuildAuthorizeUrl_GivenState_IncludesClientIdScopesStateAndRedirect()
    {
        var (provider, _) = BuildProvider();

        var url = provider.BuildAuthorizeUrl("s1");

        url.Should().StartWith("https://accounts.google.com/o/oauth2/v2/auth?");
        url.Should().Contain("client_id=google-client-id");
        url.Should().Contain($"scope={Uri.EscapeDataString("openid email profile")}");
        url.Should().Contain("state=s1");
        url.Should().Contain($"redirect_uri={Uri.EscapeDataString("http://localhost:5000/auth/google/callback")}");
    }

    [Fact]
    public async Task ExchangeCodeAsync_TokenEndpointReturnsAccessToken_FetchesUserInfo()
    {
        var (provider, handler) = BuildProvider();
        handler
            .OnPost("https://oauth2.googleapis.com/token", HttpStatusCode.OK,
                "{\"access_token\":\"gt-abc\",\"token_type\":\"Bearer\"}")
            .OnGet("https://openidconnect.googleapis.com/v1/userinfo", HttpStatusCode.OK,
                "{\"sub\":\"g-123\",\"email\":\"carol@example.com\",\"name\":\"Carol\",\"picture\":\"http://pic\"}");

        var info = await provider.ExchangeCodeAsync("c", null, CancellationToken.None);

        info.ProviderUserId.Should().Be("g-123");
        info.Email.Should().Be("carol@example.com");
        info.Username.Should().Be("Carol");
        info.AvatarUrl.Should().Be("http://pic");
    }

    [Fact]
    public async Task ExchangeCodeAsync_TokenEndpoint4xx_Throws()
    {
        var (provider, handler) = BuildProvider();
        handler.OnPost("https://oauth2.googleapis.com/token", HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\"}");

        var act = () => provider.ExchangeCodeAsync("c", null, CancellationToken.None);

        await act.Should().ThrowAsync<OAuthExchangeException>();
    }

    [Fact]
    public async Task ExchangeCodeAsync_NoName_UsesEmailLocalPart()
    {
        var (provider, handler) = BuildProvider();
        handler
            .OnPost("https://oauth2.googleapis.com/token", HttpStatusCode.OK,
                "{\"access_token\":\"t\",\"token_type\":\"Bearer\"}")
            .OnGet("https://openidconnect.googleapis.com/v1/userinfo", HttpStatusCode.OK,
                "{\"sub\":\"g\",\"email\":\"dave@example.com\",\"name\":null,\"picture\":null}");

        var info = await provider.ExchangeCodeAsync("c", null, CancellationToken.None);

        info.Username.Should().Be("dave");
    }

    [Fact]
    public async Task ExchangeCodeAsync_UserInfo4xx_Throws()
    {
        var (provider, handler) = BuildProvider();
        handler
            .OnPost("https://oauth2.googleapis.com/token", HttpStatusCode.OK,
                "{\"access_token\":\"t\",\"token_type\":\"Bearer\"}")
            .OnGet("https://openidconnect.googleapis.com/v1/userinfo", HttpStatusCode.Unauthorized,
                "{\"error\":\"unauthorized\"}");

        var act = () => provider.ExchangeCodeAsync("c", null, CancellationToken.None);

        await act.Should().ThrowAsync<OAuthExchangeException>()
            .Where(e => e.Message.Contains("userinfo failed"));
    }
}
