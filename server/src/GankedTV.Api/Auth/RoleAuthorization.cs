using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using Microsoft.AspNetCore.Authorization;

namespace GankedTV.Api.Auth;

// Registers the two role-based authorization policies in one place so callers can
// .RequireAuthorization(RolePolicies.Admin / Moderator) without each module duplicating the
// claim-matching logic. Moderator accepts admin too (admins are a superset of moderators).
public static class RoleAuthorization
{
    public static AuthorizationOptions AddRolePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(RolePolicies.Admin, p =>
            p.RequireAuthenticatedUser().RequireClaim(JwtClaims.Role, UserRoles.Admin));

        options.AddPolicy(RolePolicies.Moderator, p =>
            p.RequireAuthenticatedUser().RequireAssertion(ctx =>
                ctx.User.HasClaim(JwtClaims.Role, UserRoles.Admin)
                || ctx.User.HasClaim(JwtClaims.Role, UserRoles.Moderator)));

        return options;
    }
}

public static class RolePolicies
{
    public const string Admin = "admin";
    public const string Moderator = "moderator";
}
