using GankedTV.Api.Auth;
using GankedTV.Api.Auth.ApiKeys;
using GankedTV.Api.Contracts.ApiKeys;
using GankedTV.Api.Problems;

namespace GankedTV.Api.Endpoints;

public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        // "interactive" pins the JWT/cookie scheme: managing keys requires a real login, so a
        // leaked API key can list or revoke but can't act on the management surface (privilege
        // containment). Keys themselves are minted by the device-authorization flow, not here.
        var group = app.MapGroup("/me/api-keys").RequireAuthorization(AuthPolicies.Interactive);
        group.MapGet("/", List);
        group.MapDelete("/{id:guid}", Revoke);
        return app;
    }

    private static async Task<IResult> List(
        System.Security.Claims.ClaimsPrincipal principal,
        ApiKeyService apiKeys,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var keys = await apiKeys.ListAsync(userId, ct);
        return Results.Ok(keys.Select(k => k.ToResponse()).ToList());
    }

    private static async Task<IResult> Revoke(
        Guid id,
        System.Security.Claims.ClaimsPrincipal principal,
        ApiKeyService apiKeys,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var revoked = await apiKeys.RevokeAsync(userId, id, ct);
        return revoked ? Results.NoContent() : ProblemResults.NotFound("not_found");
    }
}
