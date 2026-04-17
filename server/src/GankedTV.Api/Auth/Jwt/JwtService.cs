using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GankedTV.Api.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GankedTV.Api.Auth.Jwt;

public sealed class JwtService : IJwtService
{
    private const int MinSecretBytes = 32;

    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;
    private readonly TokenValidationParameters _validationParameters;
    private readonly JwtSecurityTokenHandler _handler = new();

    public JwtService(IOptions<JwtOptions> options)
    {
        _options = options.Value;

        var secretBytes = Encoding.UTF8.GetBytes(_options.Secret);
        if (secretBytes.Length < MinSecretBytes)
        {
            throw new InvalidOperationException(
                $"JWT_SECRET must be at least {MinSecretBytes} bytes (got {secretBytes.Length}).");
        }

        var key = new SymmetricSecurityKey(secretBytes);
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };

        _handler.InboundClaimTypeMap.Clear();
        _handler.OutboundClaimTypeMap.Clear();
    }

    public string Issue(User user)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("name", user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
        }

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_options.ExpiryMinutes),
            signingCredentials: _credentials);

        return _handler.WriteToken(token);
    }

    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            var principal = _handler.ValidateToken(token, _validationParameters, out _);
            return principal;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }
}

public static class JwtClaims
{
    public const string Sub = JwtRegisteredClaimNames.Sub;
    public const string Name = "name";
    public const string Email = JwtRegisteredClaimNames.Email;
}
