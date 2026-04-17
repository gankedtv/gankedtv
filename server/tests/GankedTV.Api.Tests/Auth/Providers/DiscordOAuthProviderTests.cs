using System.Net;
using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Auth.Providers;

public class DiscordOAuthProviderTests
{
    private static (DiscordOAuthProvider provider, TestHttpMessageHandler handler) BuildProvider()
    {
        var handler = new TestHttpMessageHandler();
        var options = Options.Create(new OAuthOptions
        {
            StateSecret = "state-secret-32-bytes-minimum-xxxxxx",
            WebOrigin = "http://localhost:5173",
            Discord = new OAuthProviderOptions
            {
                ClientId = "discord-client-id",
                ClientSecret = "discord-client-secret",
                RedirectUri = "http://localhost:5000/auth/discord/callback",
            },
        });
        var factory = FakeHttpClientFactory.Create(handler);
        return (new DiscordOAuthProvider(factory, options), handler);
    }

    [Fact]
    public void BuildAuthorizeUrl_GivenState_IncludesClientIdScopesStateAndRedirect()
    {
        var (provider, _) = BuildProvider();

        var url = provider.BuildAuthorizeUrl("state-value");

        url.Should().StartWith("https://discord.com/oauth2/authorize?");
        url.Should().Contain("client_id=discord-client-id");
        url.Should().Contain($"redirect_uri={Uri.EscapeDataString("http://localhost:5000/auth/discord/callback")}");
        url.Should().Contain("response_type=code");
        url.Should().Contain($"scope={Uri.EscapeDataString("identify email")}");
        url.Should().Contain("state=state-value");
    }

    [Fact]
    public void BuildAuthorizeUrl_DoesNotForceSilentPrompt()
    {
        var (provider, _) = BuildProvider();

        var url = provider.BuildAuthorizeUrl("s");

        // prompt=none would break first-time sign-ins (Discord returns interaction_required
        // for users who have never authorized the app).
        url.Should().NotContain("prompt=none");
    }

    [Fact]
    public async Task ExchangeCodeAsync_TokenEndpointReturnsAccessToken_FetchesUserInfo()
    {
        var (provider, handler) = BuildProvider();
        handler
            .OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.OK,
                "{\"access_token\":\"token-abc\",\"token_type\":\"Bearer\"}")
            .OnGet("https://discord.com/api/users/@me", HttpStatusCode.OK,
                "{\"id\":\"100\",\"username\":\"alice\",\"email\":\"alice@example.com\",\"avatar\":null,\"verified\":true}");

        var info = await provider.ExchangeCodeAsync("auth-code", null, CancellationToken.None);

        info.ProviderUserId.Should().Be("100");
        info.Username.Should().Be("alice");
        info.Email.Should().Be("alice@example.com");
        info.EmailVerified.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
        var userReq = handler.Requests[1];
        userReq.Headers.Authorization!.Scheme.Should().Be("Bearer");
        userReq.Headers.Authorization!.Parameter.Should().Be("token-abc");
    }

    [Fact]
    public async Task ExchangeCodeAsync_UnverifiedDiscordAccount_SurfacesEmailVerifiedFalse()
    {
        var (provider, handler) = BuildProvider();
        handler
            .OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.OK,
                "{\"access_token\":\"t\",\"token_type\":\"Bearer\"}")
            .OnGet("https://discord.com/api/users/@me", HttpStatusCode.OK,
                "{\"id\":\"1\",\"username\":\"u\",\"email\":\"u@example.com\",\"avatar\":null,\"verified\":false}");

        var info = await provider.ExchangeCodeAsync("code", null, CancellationToken.None);

        info.EmailVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ExchangeCodeAsync_TokenError_DoesNotLeakRawBody()
    {
        var (provider, handler) = BuildProvider();
        handler.OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"secret_field\":\"SHOULD_NOT_LEAK\"}");

        var act = () => provider.ExchangeCodeAsync("bad", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OAuthExchangeException>()).Which;
        ex.Message.Should().Contain("invalid_grant");
        ex.Message.Should().NotContain("SHOULD_NOT_LEAK");
    }

    [Fact]
    public async Task ExchangeCodeAsync_TokenError_IncludesErrorDescription()
    {
        var (provider, handler) = BuildProvider();
        handler.OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"error_description\":\"Code has been consumed\"}");

        var act = () => provider.ExchangeCodeAsync("bad", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OAuthExchangeException>()).Which;
        ex.Message.Should().Contain("invalid_grant");
        ex.Message.Should().Contain("Code has been consumed");
    }

    [Fact]
    public async Task ExchangeCodeAsync_UserInfoError_ParsesDiscordRestShape()
    {
        var (provider, handler) = BuildProvider();
        // Discord's REST errors (e.g. userinfo) use {message, code}, not the OAuth2
        // {error, error_description} shape. Parser must recognise both.
        handler
            .OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.OK,
                "{\"access_token\":\"t\",\"token_type\":\"Bearer\"}")
            .OnGet("https://discord.com/api/users/@me", HttpStatusCode.Unauthorized,
                "{\"message\":\"401: Unauthorized\",\"code\":0}");

        var act = () => provider.ExchangeCodeAsync("code", null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OAuthExchangeException>()).Which;
        ex.Message.Should().Contain("401: Unauthorized");
        ex.Message.Should().Contain("userinfo failed");
    }

    [Fact]
    public async Task ExchangeCodeAsync_TokenEndpoint4xx_Throws()
    {
        var (provider, handler) = BuildProvider();
        handler.OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\"}");

        var act = () => provider.ExchangeCodeAsync("bad-code", null, CancellationToken.None);

        await act.Should().ThrowAsync<OAuthExchangeException>()
            .Where(e => e.Message.Contains("token exchange failed"));
    }

    [Fact]
    public async Task ExchangeCodeAsync_UserHasAvatarHash_BuildsCdnUrl()
    {
        var (provider, handler) = BuildProvider();
        handler
            .OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.OK,
                "{\"access_token\":\"token\",\"token_type\":\"Bearer\"}")
            .OnGet("https://discord.com/api/users/@me", HttpStatusCode.OK,
                "{\"id\":\"42\",\"username\":\"bob\",\"email\":null,\"avatar\":\"abc123\"}");

        var info = await provider.ExchangeCodeAsync("code", null, CancellationToken.None);

        info.AvatarUrl.Should().Be("https://cdn.discordapp.com/avatars/42/abc123.png");
    }

    [Fact]
    public async Task ExchangeCodeAsync_AnimatedAvatarHash_BuildsGifUrl()
    {
        var (provider, handler) = BuildProvider();
        handler
            .OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.OK,
                "{\"access_token\":\"token\",\"token_type\":\"Bearer\"}")
            .OnGet("https://discord.com/api/users/@me", HttpStatusCode.OK,
                "{\"id\":\"42\",\"username\":\"bob\",\"email\":null,\"avatar\":\"a_deadbeef\"}");

        var info = await provider.ExchangeCodeAsync("code", null, CancellationToken.None);

        info.AvatarUrl.Should().Be("https://cdn.discordapp.com/avatars/42/a_deadbeef.gif");
    }

    [Fact]
    public async Task ExchangeCodeAsync_TokenPostSendsFormEncodedBody()
    {
        var (provider, handler) = BuildProvider();
        handler
            .OnPost("https://discord.com/api/oauth2/token", HttpStatusCode.OK,
                "{\"access_token\":\"t\",\"token_type\":\"Bearer\"}")
            .OnGet("https://discord.com/api/users/@me", HttpStatusCode.OK,
                "{\"id\":\"1\",\"username\":\"u\",\"email\":null,\"avatar\":null}");

        await provider.ExchangeCodeAsync("my-code", null, CancellationToken.None);

        handler.CapturedBodies.Should().NotBeEmpty();
        var (contentType, body) = handler.CapturedBodies[0];
        contentType.Should().Be("application/x-www-form-urlencoded");
        body.Should().Contain("code=my-code");
        body.Should().Contain("grant_type=authorization_code");
        body.Should().Contain("client_id=discord-client-id");
    }
}
