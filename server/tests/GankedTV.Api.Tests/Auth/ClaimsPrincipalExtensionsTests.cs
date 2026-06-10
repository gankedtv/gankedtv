using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using GankedTV.Api.Auth;

namespace GankedTV.Api.Tests.Auth;

public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal BuildPrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public void TryGetUserId_SubClaim_ParsesGuid()
    {
        var id = Guid.NewGuid();
        var principal = BuildPrincipal(new Claim(JwtRegisteredClaimNames.Sub, id.ToString()));

        principal.TryGetUserId(out var userId).Should().BeTrue();
        userId.Should().Be(id);
    }

    [Fact]
    public void TryGetUserId_NameIdentifierFallback_ParsesGuid()
    {
        var id = Guid.NewGuid();
        var principal = BuildPrincipal(new Claim(ClaimTypes.NameIdentifier, id.ToString()));

        principal.TryGetUserId(out var userId).Should().BeTrue();
        userId.Should().Be(id);
    }

    [Fact]
    public void TryGetUserId_SubWinsOverNameIdentifier()
    {
        var subId = Guid.NewGuid();
        var principal = BuildPrincipal(
            new Claim(JwtRegisteredClaimNames.Sub, subId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        principal.TryGetUserId(out var userId).Should().BeTrue();
        userId.Should().Be(subId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void TryGetUserId_UnparseableSub_ReturnsFalse(string sub)
    {
        var principal = BuildPrincipal(new Claim(JwtRegisteredClaimNames.Sub, sub));

        principal.TryGetUserId(out var userId).Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryGetUserId_NoClaims_ReturnsFalse()
    {
        BuildPrincipal().TryGetUserId(out var userId).Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }
}
