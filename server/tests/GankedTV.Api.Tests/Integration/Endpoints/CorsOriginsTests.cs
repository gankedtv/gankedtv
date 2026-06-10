using System.Net.Http;
using FluentAssertions;
using GankedTV.Api.Tests.TestSupport;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresServices")]
public class CorsOriginsTests
{
    private readonly PostgresFixture _fx;

    public CorsOriginsTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task MultiOrigin_AllowsEachListedOrigin()
    {
        Environment.SetEnvironmentVariable("CORS_ORIGINS", "http://a.test,http://b.test");
        try
        {
            await using var factory = new AuthApiFactory(_fx.ConnectionString);
            using var client = factory.CreateClient();

            (await PreflightAllowOrigin(client, "http://a.test")).Should().Be("http://a.test");
            (await PreflightAllowOrigin(client, "http://b.test")).Should().Be("http://b.test");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        }
    }

    [Fact]
    public async Task UnlistedOrigin_IsNotAllowed()
    {
        Environment.SetEnvironmentVariable("CORS_ORIGINS", "http://a.test");
        try
        {
            await using var factory = new AuthApiFactory(_fx.ConnectionString);
            using var client = factory.CreateClient();

            (await PreflightAllowOrigin(client, "http://evil.test")).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        }
    }

    [Fact]
    public async Task Unset_FallsBackToWebOrigin()
    {
        // CORS_ORIGINS not set → the policy should accept the default WebOrigin set by AuthApiFactory.
        Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        await using var factory = new AuthApiFactory(_fx.ConnectionString);
        using var client = factory.CreateClient();

        (await PreflightAllowOrigin(client, "http://localhost:5173")).Should().Be("http://localhost:5173");
    }

    [Fact]
    public async Task CorsOrigins_AlwaysUnionsWebOrigin()
    {
        // Operator sets CORS_ORIGINS without including WEB_ORIGIN. The parser still adds
        // WEB_ORIGIN because OAuth callback pages served from it need to XHR the API — if
        // we stripped it out, the sign-in flow would silently break.
        Environment.SetEnvironmentVariable("CORS_ORIGINS", "http://ops.test");
        try
        {
            await using var factory = new AuthApiFactory(_fx.ConnectionString);
            using var client = factory.CreateClient();

            (await PreflightAllowOrigin(client, "http://ops.test")).Should().Be("http://ops.test");
            (await PreflightAllowOrigin(client, "http://localhost:5173"))
                .Should().Be("http://localhost:5173");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        }
    }

    [Fact]
    public async Task Wildcard_IsTreatedAsLiteralOrigin_NotBlanketAllow()
    {
        // "*" in CORS_ORIGINS is a literal origin string, not a wildcard. A real request
        // from any host other than the literal "*" must be rejected — otherwise combining
        // AllowCredentials() + wildcard would silently disable CORS protection.
        Environment.SetEnvironmentVariable("CORS_ORIGINS", "*");
        try
        {
            await using var factory = new AuthApiFactory(_fx.ConnectionString);
            using var client = factory.CreateClient();

            (await PreflightAllowOrigin(client, "http://evil.test")).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        }
    }

    [Theory]
    [InlineData("http://localhost:5493")] // a worktree's Vite port
    [InlineData("http://localhost:9999")] // a one-off dev override
    [InlineData("http://127.0.0.1:5173")]
    public async Task Development_AllowsAnyLocalhostOrigin_EvenWhenNotInAllowlist(string origin)
    {
        // Regression guard for the worktree foot-gun: a developer running with a non-default
        // VITE_PORT (or no WEB_ORIGIN at all) should not surface an opaque CORS error in
        // the browser. Production still enforces the strict allowlist — covered below.
        Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        await using var factory = new AuthApiFactory(_fx.ConnectionString, environment: "Development");
        using var client = factory.CreateClient();

        (await PreflightAllowOrigin(client, origin)).Should().Be(origin);
    }

    [Fact]
    public async Task Production_DoesNotAutoAllowLocalhost_StrictAllowlistOnly()
    {
        // Mirror of the dev-mode test above for production: any localhost origin not on
        // the explicit allowlist is denied. Prod has no reason to receive localhost
        // requests and we don't want the dev convenience to leak into deployed envs.
        Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        await using var factory = new AuthApiFactory(_fx.ConnectionString, environment: "Production");
        using var client = factory.CreateClient();

        // localhost:5173 IS the WebOrigin (AuthApiFactory sets WEB_ORIGIN=http://localhost:5173),
        // so it's allowed via the regular allowlist path — verify that still works.
        (await PreflightAllowOrigin(client, "http://localhost:5173")).Should().Be("http://localhost:5173");

        // A different localhost port is NOT on the allowlist and must be rejected in prod.
        (await PreflightAllowOrigin(client, "http://localhost:9999")).Should().BeNull();
    }

    private static async Task<string?> PreflightAllowOrigin(HttpClient client, string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Options, "/clips/feed");
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", "GET");

        var resp = await client.SendAsync(req);
        // A 5xx here would also produce no ACAO header; asserting 2xx first prevents the
        // test from silently passing "origin denied" when the real fault was a server error.
        resp.IsSuccessStatusCode.Should().BeTrue($"preflight should not 500 for origin {origin}; got {resp.StatusCode}");
        return resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
            ? values.FirstOrDefault()
            : null;
    }
}
