using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GankedTV.Api.Endpoints;

public static class MeEndpoints
{
    private const int MaxAvatarUrlLength = 2048;
    private const int MaxBioLength = 500;

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

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetMe(
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
            // JWT sub points at a user that no longer exists — treat as re-auth rather than
            // 404 so the SPA can drop tokens and redirect to sign-in.
            return TypedResults.Unauthorized();
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
            return Results.Unauthorized();
        }

        var changed = false;

        if (req.Username is not null)
        {
            // Reject whitespace-only input explicitly — Slugify would otherwise return the
            // fallback ("player") and silently rename the user.
            if (string.IsNullOrWhiteSpace(req.Username))
            {
                return Results.BadRequest(new { error = "invalid_username" });
            }
            var slug = UsernameGenerator.Slugify(req.Username);
            // Slugify caps at MaxLength (≤ 24 chars, under the 30-char DB column), so the only
            // length invariant to check is the fallback escape hatch for unusable input.
            // Accept literal "player" from the client; reject other input that decays to it.
            if (slug == UsernameGenerator.Fallback && !req.Username.Equals(UsernameGenerator.Fallback, StringComparison.OrdinalIgnoreCase))
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
                changed = true;
            }
        }

        if (req.Bio is not null)
        {
            if (req.Bio.Length > MaxBioLength)
            {
                return Results.BadRequest(new { error = "bio_too_long" });
            }
            var newBio = req.Bio.Length == 0 ? null : req.Bio;
            if (user.Bio != newBio)
            {
                user.Bio = newBio;
                changed = true;
            }
        }

        if (req.AvatarUrl is not null)
        {
            var (ok, newAvatar) = ValidateAvatarUrl(req.AvatarUrl);
            if (!ok)
            {
                return Results.BadRequest(new { error = "invalid_avatar_url" });
            }
            if (user.AvatarUrl != newAvatar)
            {
                user.AvatarUrl = newAvatar;
                changed = true;
            }
        }

        if (!changed)
        {
            return Results.Ok(ToResponse(user));
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUsernameUniqueViolation(ex))
        {
            // A concurrent writer took the username between our AnyAsync check and the save.
            return Results.Conflict(new { error = "username_taken" });
        }
        return Results.Ok(ToResponse(user));
    }

    private static (bool ok, string? value) ValidateAvatarUrl(string raw)
    {
        if (raw.Length == 0)
        {
            return (true, null);
        }
        if (raw.Length > MaxAvatarUrlLength)
        {
            return (false, null);
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return (false, null);
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return (false, null);
        }
        // Credentials in a URL render as an Authorization header when the browser fetches the
        // image; refuse to store them. Fragments are client-only and serve no purpose for an
        // image src, so refuse those too rather than quietly storing dead bytes.
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return (false, null);
        }
        return (true, raw);
    }

    private static bool IsUsernameUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(pg.ConstraintName, "idx_users_username", StringComparison.Ordinal);

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
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

    private static MeResponse ToResponse(User user) => new(
        user.Id,
        user.Username,
        user.Email,
        user.Bio,
        user.AvatarUrl,
        user.CreatedAt);
}
