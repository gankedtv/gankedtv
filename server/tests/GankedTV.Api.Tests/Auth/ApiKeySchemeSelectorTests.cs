using FluentAssertions;
using GankedTV.Api.Auth.ApiKeys;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;

namespace GankedTV.Api.Tests.Auth;

public class ApiKeySchemeSelectorTests
{
    private static HttpRequest RequestWith(Action<HttpRequest> configure)
    {
        var ctx = new DefaultHttpContext();
        configure(ctx.Request);
        return ctx.Request;
    }

    [Fact]
    public void XApiKeyHeader_RoutesToApiKeyScheme()
    {
        var req = RequestWith(r => r.Headers[ApiKeyDefaults.HeaderName] = "gtv_whatever");
        ApiKeyDefaults.SelectScheme(req).Should().Be(ApiKeyDefaults.Scheme);
    }

    [Fact]
    public void EmptyXApiKeyHeader_StillRoutesToApiKeyScheme()
    {
        // Presence, not content, decides routing; the handler rejects an empty value with 401.
        var req = RequestWith(r => r.Headers[ApiKeyDefaults.HeaderName] = "");
        ApiKeyDefaults.SelectScheme(req).Should().Be(ApiKeyDefaults.Scheme);
    }

    [Fact]
    public void BearerWithGtvPrefix_RoutesToApiKeyScheme()
    {
        var req = RequestWith(r => r.Headers.Authorization = $"Bearer {ApiKeyService.KeyPrefix}abc123");
        ApiKeyDefaults.SelectScheme(req).Should().Be(ApiKeyDefaults.Scheme);
    }

    [Fact]
    public void BearerJwt_RoutesToJwtBearerScheme()
    {
        var req = RequestWith(r => r.Headers.Authorization = "Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig");
        ApiKeyDefaults.SelectScheme(req).Should().Be(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void NoCredentials_RoutesToJwtBearerScheme()
    {
        var req = RequestWith(_ => { });
        ApiKeyDefaults.SelectScheme(req).Should().Be(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void LowercaseBearerScheme_FallsThroughToJwtBearer()
    {
        // Canonical clients send "Bearer"; a non-canonical "bearer gtv_" isn't matched here and
        // falls to the JWT handler (which also won't validate it) — documents the exact boundary.
        var req = RequestWith(r => r.Headers.Authorization = $"bearer {ApiKeyService.KeyPrefix}abc");
        ApiKeyDefaults.SelectScheme(req).Should().Be(JwtBearerDefaults.AuthenticationScheme);
    }
}
