using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using FluentAssertions;
using GankedTV.Api.Clips;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GankedTV.Api.Tests.Clips;

// Covers branches in ResolvePartitionKey that the integration tests can't reach: the auth
// middleware in the real pipeline runs before the limiter and rejects unauthenticated callers
// with 401, so the per-IP and "unknown" fallbacks never execute through HTTP. These unit tests
// drive the static helper directly with a constructed HttpContext to exercise every branch.
public class ClipsRateLimitingTests
{
    [Fact]
    public void ResolvePartitionKey_UsesSubClaim_WhenPresent()
    {
        var ctx = MakeContext(
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "user-123")],
            remoteIp: IPAddress.Parse("10.0.0.1"));

        ClipsRateLimiting.ResolvePartitionKey(ctx).Should().Be("u:user-123");
    }

    [Fact]
    public void ResolvePartitionKey_FallsBackToNameIdentifier_WhenSubMissing()
    {
        // Some JWT issuers / ClaimsPrincipal pipelines map `sub` to `ClaimTypes.NameIdentifier`
        // instead of leaving it as the registered name. The helper mirrors the same precedence
        // used by TryGetUserId in every authed endpoint, so they stay in lockstep.
        var ctx = MakeContext(
            claims: [new Claim(ClaimTypes.NameIdentifier, "nid-456")],
            remoteIp: IPAddress.Parse("10.0.0.2"));

        ClipsRateLimiting.ResolvePartitionKey(ctx).Should().Be("u:nid-456");
    }

    [Fact]
    public void ResolvePartitionKey_FallsBackToRemoteIp_WhenNoUserClaim()
    {
        // Defensive path: if RequireAuthorization were ever removed from a /clips group, an
        // unauthenticated caller would reach the limiter. Partition by IP rather than by null,
        // which would collapse every anonymous caller into one shared bucket.
        var ctx = MakeContext(claims: [], remoteIp: IPAddress.Parse("198.51.100.7"));

        ClipsRateLimiting.ResolvePartitionKey(ctx).Should().Be("ip:198.51.100.7");
    }

    [Fact]
    public void ResolvePartitionKey_FallsBackToUnknown_WhenNoClaimsAndNoRemoteIp()
    {
        // RemoteIpAddress is null on test rigs / unrecognised proxies. Without a stable string
        // bucket here, the partition function would throw or null-bucket every such caller.
        var ctx = MakeContext(claims: [], remoteIp: null);

        ClipsRateLimiting.ResolvePartitionKey(ctx).Should().Be("ip:unknown");
    }

    [Fact]
    public void ResolveIpPartitionKey_UsesRemoteIp()
    {
        // The view-policy partition is IP-only — auth claims are ignored so a user behind
        // shared NAT can't burn through the per-IP bucket faster than the anon equivalent.
        var ctx = MakeContext(
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "should-be-ignored")],
            remoteIp: IPAddress.Parse("192.0.2.7"));

        ClipsRateLimiting.ResolveIpPartitionKey(ctx).Should().Be("ip:192.0.2.7");
    }

    [Fact]
    public void ResolveIpPartitionKey_FallsBackToUnknown_WhenRemoteIpMissing()
    {
        var ctx = MakeContext(claims: [], remoteIp: null);

        ClipsRateLimiting.ResolveIpPartitionKey(ctx).Should().Be("ip:unknown");
    }

    [Fact]
    public void ResolvePartitionKey_TreatsEmptySubAsMissing()
    {
        // ClaimsPrincipal.FindFirst returns the claim even when its Value is empty. Treating an
        // empty sub as present would partition every such caller into the literal "u:" bucket.
        var ctx = MakeContext(
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "")],
            remoteIp: IPAddress.Parse("203.0.113.4"));

        ClipsRateLimiting.ResolvePartitionKey(ctx).Should().Be("ip:203.0.113.4");
    }

    private static HttpContext MakeContext(IEnumerable<Claim> claims, IPAddress? remoteIp)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        };
        ctx.Connection.RemoteIpAddress = remoteIp;
        return ctx;
    }
}
