using System.Net;
using System.Net.Http.Json;
using System.Web;
using FluentAssertions;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresAuth")]
public class AuthEndpointsCallbackTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public AuthEndpointsCallbackTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }

    private static readonly OAuthUserInfo HappyInfo = new(
        ProviderUserId: "d-happy-1",
        Email: "happy@example.com",
        Username: "Happy",
        AvatarUrl: null,
        EmailVerified: true);

    // Caller invokes this exactly once per test (xUnit creates a new instance per [Fact]),
    // so guarding against a second assignment keeps the "one factory per test" contract
    // visible: if a future test accidentally calls it twice, the assert fires instead of
    // silently leaking the first WebApplicationFactory.
    private HttpClient BuildClientWithFake(IOAuthProvider fake, bool handleCookies = true)
    {
        Assert.Null(_factory);
        _factory = new AuthApiFactory(
            _fx.ConnectionString,
            oauthProviders: new[] { fake });

        return _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = handleCookies,
        });
    }

    private static async Task<string> StartAndExtractStateAsync(HttpClient client, string provider, string? returnTo = null)
    {
        var url = returnTo is null ? $"/auth/{provider}/start" : $"/auth/{provider}/start?returnTo={Uri.EscapeDataString(returnTo)}";
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        return HttpUtility.ParseQueryString(location.Query).Get("state")!;
    }

    [Fact]
    public async Task Callback_MissingCode_Returns400()
    {
        await _fx.ResetAsync();
        using var client = BuildClientWithFake(FakeOAuthProvider.Returning("discord", HappyInfo));

        var resp = await client.GetAsync("/auth/discord/callback?state=abc");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("missing_code_or_state");
    }

    [Fact]
    public async Task Callback_MissingState_Returns400()
    {
        await _fx.ResetAsync();
        using var client = BuildClientWithFake(FakeOAuthProvider.Returning("discord", HappyInfo));

        var resp = await client.GetAsync("/auth/discord/callback?code=xyz");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("missing_code_or_state");
    }

    [Fact]
    public async Task Callback_UnknownProvider_Returns404()
    {
        await _fx.ResetAsync();
        using var client = BuildClientWithFake(FakeOAuthProvider.Returning("discord", HappyInfo));

        var resp = await client.GetAsync("/auth/twitter/callback?code=c&state=s");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("unknown_provider");
    }

    [Fact]
    public async Task Callback_NoCookie_Returns400InvalidState()
    {
        await _fx.ResetAsync();
        // handleCookies: false → no cookie jar, so even if a previous request set
        // the state cookie it wouldn't round-trip to the callback.
        using var client = BuildClientWithFake(
            FakeOAuthProvider.Returning("discord", HappyInfo),
            handleCookies: false);

        var resp = await client.GetAsync("/auth/discord/callback?code=c&state=unpaired");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_state");
    }

    [Fact]
    public async Task Callback_MismatchedStateAndCookie_Returns400()
    {
        await _fx.ResetAsync();
        using var client = BuildClientWithFake(FakeOAuthProvider.Returning("discord", HappyInfo));

        // Run /start twice; the cookie holds the most recent state — the first state is now
        // stale and must not validate against the current cookie.
        var stale = await StartAndExtractStateAsync(client, "discord", returnTo: "/feed");
        _ = await StartAndExtractStateAsync(client, "discord", returnTo: "/other");

        var resp = await client.GetAsync($"/auth/discord/callback?code=c&state={Uri.EscapeDataString(stale)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_state");
    }

    [Fact]
    public async Task Callback_ExchangeFails_Returns400OAuthExchangeFailed()
    {
        await _fx.ResetAsync();
        using var client = BuildClientWithFake(
            FakeOAuthProvider.Throwing("discord", new OAuthExchangeException("Discord token exchange failed (400).")));

        var state = await StartAndExtractStateAsync(client, "discord");
        var resp = await client.GetAsync($"/auth/discord/callback?code=bad&state={Uri.EscapeDataString(state)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("oauth_exchange_failed");
    }

    [Fact]
    public async Task Callback_Happy_RedirectsToWebWithTokenAndRefresh()
    {
        await _fx.ResetAsync();
        using var client = BuildClientWithFake(FakeOAuthProvider.Returning("discord", HappyInfo));

        var state = await StartAndExtractStateAsync(client, "discord");
        var resp = await client.GetAsync($"/auth/discord/callback?code=c&state={Uri.EscapeDataString(state)}");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().StartWith("http://localhost:5173/auth/callback?");
        location.Should().Contain("token=");
        location.Should().Contain("refresh=");

        await using var db = _fx.CreateContext();
        var user = await db.Users.AsNoTracking().SingleAsync();
        user.DiscordId.Should().Be("d-happy-1");
        user.Email.Should().Be("happy@example.com");
    }

    [Fact]
    public async Task Callback_WithReturnTo_PropagatesToRedirect()
    {
        await _fx.ResetAsync();
        using var client = BuildClientWithFake(FakeOAuthProvider.Returning("discord", HappyInfo));

        var state = await StartAndExtractStateAsync(client, "discord", returnTo: "/clip/abc");
        var resp = await client.GetAsync($"/auth/discord/callback?code=c&state={Uri.EscapeDataString(state)}");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().Contain($"returnTo={Uri.EscapeDataString("/clip/abc")}");
    }

    [Fact]
    public async Task Callback_DeletesStateCookieAfterUse()
    {
        await _fx.ResetAsync();
        using var client = BuildClientWithFake(FakeOAuthProvider.Returning("discord", HappyInfo));

        var state = await StartAndExtractStateAsync(client, "discord");
        var resp = await client.GetAsync($"/auth/discord/callback?code=c&state={Uri.EscapeDataString(state)}");

        resp.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        // Response.Cookies.Delete emits an expired cookie — asserting just "gtv_oauth_state="
        // would also match a newly-issued one. Pin on the expiry signal so a regression where
        // the handler re-issues the cookie (instead of deleting it) actually fails this test.
        var stateCookie = cookies!.SingleOrDefault(c => c.StartsWith("gtv_oauth_state=", StringComparison.Ordinal));
        stateCookie.Should().NotBeNull();
        stateCookie!.Should().MatchRegex("expires=|max-age=0");
    }

    [Fact]
    public async Task Refresh_NullBody_Returns400()
    {
        await _fx.ResetAsync();
        // Uses the real providers (no override) since /auth/refresh doesn't touch OAuth state.
        Assert.Null(_factory);
        _factory = new AuthApiFactory(_fx.ConnectionString);
        using var client = _factory.CreateClient();

        // JSON literal `null` deserialises to a null RefreshRequest — hit the req-is-null guard
        // that otherwise only the empty-refresh path covers.
        using var body = new StringContent("null", System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/auth/refresh", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
