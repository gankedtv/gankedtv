using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Auth;

// Single source of truth for the identity claim set. Both the JWT issuer and the API-key
// authentication handler build principals from this so the two auth paths can't drift —
// every downstream reader (TryGetUserId on "sub", the role policies on "role", the
// rate-limiter partition on "sub", NameClaimType="name") sees an identical shape.
public static class UserClaims
{
    public static List<Claim> Build(User user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtClaims.Name, user.Username),
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
        }
        // User.Role is non-null and defaults to "user", so in practice this is always emitted and
        // authorization policies can assert on its presence. The guard only skips a blank role,
        // which would be malformed data — never a valid claim to emit.
        if (!string.IsNullOrWhiteSpace(user.Role))
        {
            claims.Add(new Claim(JwtClaims.Role, user.Role));
        }
        return claims;
    }
}
