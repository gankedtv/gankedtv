using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using GankedTV.Api.Auth.Passwords;
using GankedTV.Api.Contracts.Auth;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresAuth")]
public class AuthEndpointsCredentialsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public AuthEndpointsCredentialsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }

    private HttpClient BuildClient()
    {
        Assert.Null(_factory);
        _factory = new AuthApiFactory(_fx.ConnectionString);
        return _factory.CreateClient();
    }

    private static RegisterRequest GoodRegister(string email = "alice@example.com", string username = "alice", string password = "correct-horse-battery") =>
        new(email, username, password);

    [Fact]
    public async Task Register_HappyPath_ReturnsTokenAndPersistsUser()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        var resp = await client.PostAsJsonAsync("/auth/register", GoodRegister());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        token!.Token.Should().NotBeNullOrEmpty();
        token.Refresh.Should().NotBeNullOrEmpty();
        token.ExpiresIn.Should().BeGreaterThan(0);

        await using var db = _fx.CreateContext();
        var user = await db.Users.AsNoTracking().SingleAsync();
        user.Email.Should().Be("alice@example.com");
        user.Username.Should().Be("alice");
        user.PasswordHash.Should().NotBeNullOrEmpty();
        user.PasswordAlgo.Should().Be("argon2id");
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_Returns409()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        (await client.PostAsJsonAsync("/auth/register", GoodRegister())).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync("/auth/register", GoodRegister(username: "alice2"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("email_taken");
    }

    [Fact]
    public async Task Register_AgainstExistingOAuthOnlyAccount_Returns409()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        // Seed an OAuth-only user (no PasswordHash).
        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "oauthonly",
                Email = "oauthonly@example.com",
                DiscordId = "d-1",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest("oauthonly@example.com", "stealer", "long-and-fine-password"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("email_taken");
    }

    [Fact]
    public async Task Register_WithCollidingUsername_AutoSuffixes()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        // First user takes "bob".
        (await client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest("bob@example.com", "bob", "long-and-fine-password"))).EnsureSuccessStatusCode();

        // Second registration with the same desired username + a different email — auto-suffix.
        var resp = await client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest("bob2@example.com", "bob", "another-fine-password"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        // Order by Email (unique) rather than CreatedAt, which can tie at high-precision-clock
        // resolution when two registrations happen back-to-back inside the same test.
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync();
        users.Should().HaveCount(2);
        var first = users.Single(u => u.Email == "bob@example.com");
        var second = users.Single(u => u.Email == "bob2@example.com");
        first.Username.Should().Be("bob");
        second.Username.Should().NotBe("bob");
        second.Username.Should().StartWith("bob-");
    }

    [Fact]
    public async Task Register_WithWeakPassword_Returns400()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        // 12+ chars (so the DataAnnotation filter doesn't pre-empt) but on the
        // policy's common-password list — exercises the endpoint's InvalidPasswordResult
        // arm rather than the validation-filter short-circuit.
        var resp = await client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest("c@example.com", "carol", "abc123abc123"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("weak_password");
    }

    [Fact]
    public async Task Register_WithBadEmail_Returns400()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        var resp = await client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest("not-an-email", "dave", "long-and-fine-password"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_TokenValidatesAgainstAuthMe_AndHasPasswordIsTrue()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        var resp = await client.PostAsJsonAsync("/auth/register", GoodRegister());
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var me = await client.GetAsync("/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        // Password-registered account → hasPassword is true. The web settings view uses
        // this to render "Change password" copy and require a current-password input.
        var body = await me.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Login_HappyPath_ReturnsToken()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        (await client.PostAsJsonAsync("/auth/register", GoodRegister())).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("alice@example.com", "correct-horse-battery"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        token!.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        (await client.PostAsJsonAsync("/auth/register", GoodRegister())).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("alice@example.com", "wrong-password-here"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_credentials");
    }

    [Fact]
    public async Task Login_WithUnknownEmail_Returns401()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        var resp = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("nobody@example.com", "doesnt-matter-12345"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_AgainstOAuthOnlyUser_Returns401()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        await using (var db = _fx.CreateContext())
        {
            db.Users.Add(new User
            {
                Username = "oauthonly",
                Email = "oauthonly@example.com",
                DiscordId = "d-2",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("oauthonly@example.com", "any-password-here"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithBadEmailFormat_Returns400FromValidationFilter()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        var resp = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("not-an-email", "anything-12345"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetPassword_OAuthOnlyUserAttachesPassword_LoginSucceedsAfter()
    {
        await _fx.ResetAsync();
        Assert.Null(_factory);
        _factory = new AuthApiFactory(_fx.ConnectionString);

        // Seed an OAuth-only user and mint a JWT for them.
        var (_, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory, "oauthuser", u =>
        {
            u.Email = "oauthuser@example.com";
            u.DiscordId = "d-3";
        });

        using var authed = AuthTestHelpers.CreateBearerClient(_factory, token);

        // Attach a password without currentPassword (allowed for OAuth-only users).
        var setResp = await authed.PostAsJsonAsync(
            "/auth/password",
            new SetPasswordRequest(null, "fresh-strong-password"));
        setResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Now /auth/login with that password works.
        using var anon = _factory.CreateClient();
        var loginResp = await anon.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("oauthuser@example.com", "fresh-strong-password"));
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetPassword_RotatesExistingPassword_RequiresCurrentPassword()
    {
        await _fx.ResetAsync();
        Assert.Null(_factory);
        _factory = new AuthApiFactory(_fx.ConnectionString);

        // Seed a user that already has a password.
        Guid userId;
        await using (var db = _fx.CreateContext())
        {
            using var scope = _factory.Services.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var user = new User
            {
                Username = "rotater",
                Email = "rotater@example.com",
                PasswordHash = hasher.Hash("original-password-123"),
                PasswordAlgo = hasher.Algorithm,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        // Mint a JWT for that user.
        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<GankedTV.Api.Auth.Jwt.IJwtService>();
            await using var db = _fx.CreateContext();
            var user = (await db.Users.FindAsync(userId))!;
            token = jwt.Issue(user);
        }
        using var authed = AuthTestHelpers.CreateBearerClient(_factory, token);

        // Wrong current password → 400.
        var bad = await authed.PostAsJsonAsync(
            "/auth/password",
            new SetPasswordRequest("not-the-original", "new-rotated-password"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("wrong_current_password");

        // Correct current password → 204, and the new password works for login.
        var good = await authed.PostAsJsonAsync(
            "/auth/password",
            new SetPasswordRequest("original-password-123", "new-rotated-password"));
        good.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var anon = _factory.CreateClient();
        var loginResp = await anon.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("rotater@example.com", "new-rotated-password"));
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetPassword_TokenForDeletedUser_Returns401()
    {
        // JWT sub points at a user that no longer exists. The analogous case in MeEndpoints
        // returns 401 (re-auth) so the SPA drops tokens and redirects; SetPassword mirrors it.
        await _fx.ResetAsync();
        Assert.Null(_factory);
        _factory = new AuthApiFactory(_fx.ConnectionString);
        var (userId, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory, "deletee");

        await using (var db = _fx.CreateContext())
        {
            var user = await db.Users.FindAsync(userId);
            db.Users.Remove(user!);
            await db.SaveChangesAsync();
        }

        using var authed = AuthTestHelpers.CreateBearerClient(_factory, token);
        var resp = await authed.PostAsJsonAsync(
            "/auth/password",
            new SetPasswordRequest(null, "fresh-strong-password"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetPassword_Unauthenticated_Returns401()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        var resp = await client.PostAsJsonAsync(
            "/auth/password",
            new SetPasswordRequest(null, "doesnt-matter-strong"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetPassword_WithWeakNewPassword_Returns400()
    {
        await _fx.ResetAsync();
        Assert.Null(_factory);
        _factory = new AuthApiFactory(_fx.ConnectionString);
        var (_, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory, "weakset");
        using var authed = AuthTestHelpers.CreateBearerClient(_factory, token);

        var resp = await authed.PostAsJsonAsync(
            "/auth/password",
            new SetPasswordRequest(null, "tiny"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_NullBody_Returns400FromValidationFilter()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        using var body = new StringContent("null", System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/auth/register", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_NullBody_Returns400FromValidationFilter()
    {
        await _fx.ResetAsync();
        using var client = BuildClient();

        using var body = new StringContent("null", System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/auth/login", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetPassword_NullBody_Returns400FromValidationFilter()
    {
        await _fx.ResetAsync();
        Assert.Null(_factory);
        _factory = new AuthApiFactory(_fx.ConnectionString);
        var (_, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory, "nullbody");
        using var authed = AuthTestHelpers.CreateBearerClient(_factory, token);

        using var body = new StringContent("null", System.Text.Encoding.UTF8, "application/json");
        var resp = await authed.PostAsync("/auth/password", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
