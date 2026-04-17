using System.IdentityModel.Tokens.Jwt;
using System.Text;
using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GankedTV.Api.Tests.Auth;

public class JwtServiceTests
{
    private const string ValidSecret = "this-is-a-test-secret-that-is-long-enough-1234";

    private static JwtService BuildService(string secret = ValidSecret, int expiryMinutes = 15) =>
        new(Options.Create(new JwtOptions
        {
            Secret = secret,
            Issuer = "gankedtv-test",
            Audience = "gankedtv-web-test",
            ExpiryMinutes = expiryMinutes,
        }));

    private static User BuildUser(string? email = "alice@example.com") => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Username = "alice",
        Email = email,
    };

    [Fact]
    public void Issue_ValidUser_ReturnsTokenWithSubAndNameClaims()
    {
        var token = BuildService().Issue(BuildUser());

        var read = new JwtSecurityTokenHandler().ReadJwtToken(token);
        read.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "11111111-1111-1111-1111-111111111111");
        read.Claims.Should().Contain(c => c.Type == "name" && c.Value == "alice");
    }

    [Fact]
    public void Issue_UserWithEmail_IncludesEmailClaim()
    {
        var token = BuildService().Issue(BuildUser(email: "alice@example.com"));

        var read = new JwtSecurityTokenHandler().ReadJwtToken(token);
        read.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Email && c.Value == "alice@example.com");
    }

    [Fact]
    public void Issue_UserWithoutEmail_OmitsEmailClaim()
    {
        var token = BuildService().Issue(BuildUser(email: null));

        var read = new JwtSecurityTokenHandler().ReadJwtToken(token);
        read.Claims.Should().NotContain(c => c.Type == JwtRegisteredClaimNames.Email);
    }

    [Fact]
    public void Validate_FreshToken_ReturnsPrincipal()
    {
        var service = BuildService();
        var token = service.Issue(BuildUser());

        var principal = service.Validate(token);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtRegisteredClaimNames.Sub)!.Value.Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public void Validate_TamperedSignature_ReturnsNull()
    {
        var service = BuildService();
        var token = service.Issue(BuildUser());

        // Flip last character to invalidate the signature.
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        service.Validate(tampered).Should().BeNull();
    }

    [Fact]
    public void Validate_ExpiredToken_ReturnsNull()
    {
        var service = BuildService();
        var now = DateTime.UtcNow;
        // Construct an already-expired token (notBefore and expires both in the past, past the 30s clock skew).
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ValidSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expired = new JwtSecurityToken(
            issuer: "gankedtv-test",
            audience: "gankedtv-web-test",
            claims: new[] { new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, "x") },
            notBefore: now.AddMinutes(-10),
            expires: now.AddMinutes(-5),
            signingCredentials: creds);
        var token = new JwtSecurityTokenHandler().WriteToken(expired);

        service.Validate(token).Should().BeNull();
    }

    [Fact]
    public void Ctor_SecretShorterThan32Bytes_Throws()
    {
        var act = () => BuildService(secret: "too-short");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JWT_SECRET*");
    }

    [Fact]
    public void Validate_DifferentSigningKey_ReturnsNull()
    {
        var issuer = BuildService();
        var validator = BuildService(secret: "a-completely-different-secret-also-32-bytes-long");
        var token = issuer.Issue(BuildUser());

        validator.Validate(token).Should().BeNull();
    }
}
