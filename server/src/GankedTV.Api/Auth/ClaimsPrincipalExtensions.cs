using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace GankedTV.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        // JwtBearer's default handler keeps the inbound claim-type map, which remaps
        // "sub" → ClaimTypes.NameIdentifier during validation. We issue tokens with the
        // map cleared (so "sub" stays "sub"), but reading from either side makes this
        // resilient to future changes to the bearer setup.
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }

    // Nullable form for anonymous-friendly read paths that still need an owner check
    // (private clips): null never matches a real owner id, so anonymous callers simply
    // fail the ownership predicate.
    public static Guid? GetUserIdOrNull(this ClaimsPrincipal principal) =>
        principal.TryGetUserId(out var userId) ? userId : null;
}
