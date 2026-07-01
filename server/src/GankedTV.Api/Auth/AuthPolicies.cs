using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace GankedTV.Api.Auth;

public static class AuthPolicies
{
    // Requires an interactive (JWT/cookie) login by pinning the JWT bearer scheme. An API key
    // arriving as `Authorization: Bearer gtv_…` is routed to the ApiKey scheme by the forward
    // selector, so it never satisfies this policy — a leaked key can't manage keys.
    public const string Interactive = "interactive";

    public static AuthorizationOptions AddInteractivePolicy(this AuthorizationOptions options)
    {
        options.AddPolicy(Interactive, p => p
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser());
        return options;
    }
}
