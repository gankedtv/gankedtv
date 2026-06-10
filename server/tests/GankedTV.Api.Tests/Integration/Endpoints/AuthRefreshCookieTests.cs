using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using GankedTV.Api.Auth.Cookies;
using GankedTV.Api.Auth.Providers;
using GankedTV.Api.Contracts.Auth;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresAuth")]
public class AuthRefreshCookieTests : IAsyncLifetime
{
    private const string TrustedOrigin = "http://localhost:5173";

    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public AuthRefreshCookieTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }

    // Cookie mode is enabled via options (not the env var) so parallel test collections
    // never observe a flipped AUTH_REFRESH_COOKIE_ENABLED — see AuthApiFactory's env contract.
    private HttpClient BuildClient(bool cookieMode = true, IOAuthProvider? oauthProvider = null)
    {
        Assert.Null(_factory);
        _factory = new AuthApiFactory(
            _fx.ConnectionString,
            oauthProviders: oauthProvider is null ? null : new[] { oauthProvider },
            configureServices: cookieMode
                ? services => services.Configure<RefreshCookieOptions>(o => o.Enabled = true)
                : null);
        return _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
    }

    private static RegisterRequest GoodRegister(string email = "cookie@example.com", string username = "cookieuser") =>
        new(email, username, "correct-horse-battery");

    private static string? ExtractRefreshCookie(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var header in cookies)
        {
            var match = Regex.Match(header, $"^{RefreshCookieService.CookieName}=([^;]*)");
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    [Fact]
    public async Task Register_CookieMode_SetsHttpOnlyCookieAndBlanksBodyRefresh()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        var resp = await client.PostAsJsonAsync("/auth/register", GoodRegister());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        body!.Token.Should().NotBeNullOrEmpty();
        body.Refresh.Should().BeEmpty();

        var setCookie = resp.Headers.GetValues("Set-Cookie")
            .Single(h => h.StartsWith(RefreshCookieService.CookieName));
        setCookie.Should().Contain("httponly");
        setCookie.Should().Contain("path=/auth");
        ExtractRefreshCookie(resp).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_CookieMode_SetsCookie()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();
        (await client.PostAsJsonAsync("/auth/register", GoodRegister())).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync(
            "/auth/login", new { email = "cookie@example.com", password = "correct-horse-battery" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ExtractRefreshCookie(resp).Should().NotBeNullOrEmpty();
        (await resp.Content.ReadFromJsonAsync<TokenResponse>())!.Refresh.Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_CookieMode_OmitsRefreshParamAndSetsCookie()
    {
        await _fx.ResetAsync();
        var info = new OAuthUserInfo("d-cookie-1", "oauth-cookie@example.com", "OauthCookie", null, true);
        using var client = BuildClient(oauthProvider: FakeOAuthProvider.Returning("discord", info));

        var startResp = await client.GetAsync("/auth/discord/start");
        var state = HttpUtility.ParseQueryString(startResp.Headers.Location!.Query).Get("state")!;

        var resp = await client.GetAsync($"/auth/discord/callback?code=abc&state={Uri.EscapeDataString(state)}");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = HttpUtility.ParseQueryString(resp.Headers.Location!.Query);
        query.Get("token").Should().NotBeNullOrEmpty();
        query.Get("refresh").Should().BeNull();
        ExtractRefreshCookie(resp).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Refresh_CookieOnly_RotatesAndOldTokenDies()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();
        var register = await client.PostAsJsonAsync("/auth/register", GoodRegister());
        var firstCookie = ExtractRefreshCookie(register)!;

        client.DefaultRequestHeaders.Add("Origin", TrustedOrigin);
        var refresh = await client.PostAsJsonAsync("/auth/refresh", new { });

        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await refresh.Content.ReadFromJsonAsync<TokenResponse>();
        body!.Token.Should().NotBeNullOrEmpty();
        body.Refresh.Should().BeEmpty();
        var rotatedCookie = ExtractRefreshCookie(refresh);
        rotatedCookie.Should().NotBeNullOrEmpty().And.NotBe(firstCookie);

        // Replaying the pre-rotation cookie value must fail (body path, no CSRF needed).
        var replay = await client.PostAsJsonAsync("/auth/refresh", new { refresh = firstCookie });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_CookieSourced_MissingOrigin_Returns403()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();
        (await client.PostAsJsonAsync("/auth/register", GoodRegister())).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync("/auth/refresh", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("csrf_origin_rejected");
    }

    [Fact]
    public async Task Refresh_CookieSourced_UntrustedOrigin_Returns403()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();
        (await client.PostAsJsonAsync("/auth/register", GoodRegister())).EnsureSuccessStatusCode();

        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        var resp = await client.PostAsJsonAsync("/auth/refresh", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refresh_BodyToken_TakesPrecedenceAndSkipsCsrf()
    {
        await _fx.ResetAsync();
        // Cookie mode OFF for issuance so the body token is real; then a separate
        // cookie-mode client would be a second factory — instead exercise precedence on
        // the same cookie-mode client: body token wins over the (stale) cookie.
        using var client = BuildClient();
        var register = await client.PostAsJsonAsync("/auth/register", GoodRegister());
        var cookieToken = ExtractRefreshCookie(register)!;

        // No Origin header: a body-sourced token must not require CSRF validation.
        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refresh = cookieToken });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_CookieMode_RevokesFamilyAndClearsCookie()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();
        var register = await client.PostAsJsonAsync("/auth/register", GoodRegister());
        var cookieValue = ExtractRefreshCookie(register)!;

        client.DefaultRequestHeaders.Add("Origin", TrustedOrigin);
        var logout = await client.PostAsJsonAsync("/auth/logout", new { });

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var deletion = logout.Headers.GetValues("Set-Cookie")
            .Single(h => h.StartsWith(RefreshCookieService.CookieName));
        deletion.Should().MatchRegex("expires=|max-age=0");

        // The revoked family must reject the old token even via the body path.
        var replay = await client.PostAsJsonAsync("/auth/refresh", new { refresh = cookieValue });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_CookieSourced_MissingOrigin_Returns403()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();
        (await client.PostAsJsonAsync("/auth/register", GoodRegister())).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync("/auth/logout", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logout_BodyMode_RevokesFamilyWithoutCookie()
    {
        await _fx.ResetAsync();
        using var client = BuildClient(cookieMode: false);
        var register = await client.PostAsJsonAsync("/auth/register", GoodRegister());
        var refresh = (await register.Content.ReadFromJsonAsync<TokenResponse>())!.Refresh;
        refresh.Should().NotBeNullOrEmpty();

        var logout = await client.PostAsJsonAsync("/auth/logout", new { refresh });

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        logout.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();

        var replay = await client.PostAsJsonAsync("/auth/refresh", new { refresh });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CookieModeOff_BehavesAsBefore()
    {
        await _fx.ResetAsync();
        using var client = BuildClient(cookieMode: false);

        var resp = await client.PostAsJsonAsync("/auth/register", GoodRegister());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        body!.Refresh.Should().NotBeNullOrEmpty();
        resp.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();
    }
}
