using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Endpoints;

public static class MeEndpoints
{
    public sealed record MeResponse(
        Guid Id,
        string Username,
        string? Email,
        string? Bio,
        string? AvatarUrl,
        DateTimeOffset CreatedAt);

    public sealed record UpdateMeRequest(string? Username, string? Bio, string? AvatarUrl);

    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", GetMe).RequireAuthorization();
        app.MapPatch("/me", PatchMe).RequireAuthorization();
        return app;
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult, NotFound>> GetMe(
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(ToResponse(user));
    }

    private static async Task<IResult> PatchMe(
        [FromBody] UpdateMeRequest req,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (req.Username is not null)
        {
            var slug = UsernameGenerator.Slugify(req.Username);
            if (slug.Length == 0 || slug.Length > 30)
            {
                return Results.BadRequest(new { error = "invalid_username" });
            }
            if (slug != user.Username)
            {
                var taken = await db.Users.AnyAsync(u => u.Id != userId && u.Username == slug, ct);
                if (taken)
                {
                    return Results.Conflict(new { error = "username_taken" });
                }
                user.Username = slug;
            }
        }

        if (req.Bio is not null)
        {
            if (req.Bio.Length > 500)
            {
                return Results.BadRequest(new { error = "bio_too_long" });
            }
            user.Bio = req.Bio.Length == 0 ? null : req.Bio;
        }

        if (req.AvatarUrl is not null)
        {
            if (req.AvatarUrl.Length == 0)
            {
                user.AvatarUrl = null;
            }
            else if (!Uri.TryCreate(req.AvatarUrl, UriKind.Absolute, out var uri) ||
                     (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest(new { error = "invalid_avatar_url" });
            }
            else
            {
                user.AvatarUrl = req.AvatarUrl;
            }
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(user));
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = default;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }

    private static MeResponse ToResponse(User user) => new(
        user.Id,
        user.Username,
        user.Email,
        user.Bio,
        user.AvatarUrl,
        user.CreatedAt);
}
