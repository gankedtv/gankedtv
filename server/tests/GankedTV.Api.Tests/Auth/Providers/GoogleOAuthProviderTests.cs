using System.Net;
using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Auth.Providers;

public class GoogleOAuthProviderTests
{
    private static (GoogleOAuthProvider provider, TestHttpMessageHandler handler) BuildProvider(
        ILogger<GoogleOAuthProvider>? logger = null)
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
        return (new GoogleOAuthProvider(factory, options, logger), handler);
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
    public async Task ExchangeCodeAsync_ErrorWithDescription_IncludesBoth()
    {
        var (provider, handler) = BuildProvider();
        handler.OnPost("https://oauth2.googleapis.com/token", HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"error_description\":\"Bad grant\"}");

        var act = () => provider.ExchangeCodeAsync("c", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OAuthExchangeException>()).Which;
        ex.Message.Should().Contain("invalid_grant");
        ex.Message.Should().Contain("Bad grant");
    }

    [Fact]
    public async Task ExchangeCodeAsync_UnparseableErrorBody_OmitsDetail()
    {
        var (provider, handler) = BuildProvider();
        handler.OnPost("https://oauth2.googleapis.com/token", HttpStatusCode.BadGateway,
            "<!DOCTYPE html>not-json");

        var act = () => provider.ExchangeCodeAsync("c", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OAuthExchangeException>()).Which;
        ex.Message.Should().Be("Google token exchange failed (502).");
    }

    [Fact]
    public async Task ExchangeCodeAsync_WithDebugLogger_EmitsDebugOnFailure()
    {
        var capturing = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capturing).SetMinimumLevel(LogLevel.Debug));
        var (provider, handler) = BuildProvider(factory.CreateLogger<GoogleOAuthProvider>());
        handler.OnPost("https://oauth2.googleapis.com/token", HttpStatusCode.BadRequest,
            "raw google body");

        var act = () => provider.ExchangeCodeAsync("c", null, CancellationToken.None);

        await act.Should().ThrowAsync<OAuthExchangeException>();
        capturing.Messages.Should().Contain(m => m.Contains("raw google body"));
    }

    [Fact]
    public async Task ExchangeCodeAsync_EmailWithoutAtSign_UsesWholeStringAsLocalPart()
    {
        var (provider, handler) = BuildProvider();
        // Odd but possible: Google's schema says email is a string, not a formatted email.
        // EmailLocalPart falls back to the whole value when there's no '@'.
        handler
            .OnPost("https://oauth2.googleapis.com/token", HttpStatusCode.OK,
                "{\"access_token\":\"t\",\"token_type\":\"Bearer\"}")
            .OnGet("https://openidconnect.googleapis.com/v1/userinfo", HttpStatusCode.OK,
                "{\"sub\":\"g\",\"email\":\"noatsign\",\"name\":null,\"picture\":null}");

        var info = await provider.ExchangeCodeAsync("c", null, CancellationToken.None);

        info.Username.Should().Be("noatsign");
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
