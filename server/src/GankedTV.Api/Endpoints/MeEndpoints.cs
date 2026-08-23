using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using GankedTV.Api.Auth;
using GankedTV.Api.Contracts.Users;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using GankedTV.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GankedTV.Api.Endpoints;

public static partial class MeEndpoints
{
    private const int MaxBioLength = 500;
    private const int MaxSocialHandleLength = 32;

    // #RRGGBB. The DB ck_users_accent_color check enforces the same shape — keep them in sync.
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex AccentColorRegex();
    // Whitelist for social handles. URL-safe but loose enough to accept the platforms we care
    // about (Twitch/YouTube/Twitter all permit dots, underscores, hyphens within these limits).
    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex SocialHandleRegex();

    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        // Mounted under /auth so the path doesn't match common tracker-blocker lists
        // (uBlock / Brave Shields / Arc / corporate DLP appliances all flag bare "/me"
        // as analytics) and so it sits in the same namespace as /auth/login etc.
        app.MapGet("/auth/me", GetMe).RequireAuthorization();
        app.MapPatch("/auth/me", PatchMe).RequireAuthorization().WithValidation<UpdateMeRequest>();
        return app;
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetMe(
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
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
        return TypedResults.Ok(user.ToMe());
    }

    private static async Task<IResult> PatchMe(
        // Nullable so a literal JSON `null` body reaches the ValidationEndpointFilter (which
        // shapes it into the same ValidationProblemDetails response as a missing field)
        // rather than surfacing as a framework-generated 400 that bypasses our filter.
        [FromBody] UpdateMeRequest? req,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return ProblemResults.Unauthorized("unauthorized");
        }

        // Defensive: the WithValidation<T> filter guards null bodies before this handler runs
        // — same envelope so a filter removal doesn't change the shape clients see.
        if (req is null)
        {
            return ProblemResults.InvalidBody();
        }

        var changed = false;

        if (req.Username is not null)
        {
            // Reject whitespace-only input explicitly — Slugify would otherwise return the
            // fallback ("player") and silently rename the user.
            if (string.IsNullOrWhiteSpace(req.Username))
            {
                return ProblemResults.BadRequest("invalid_username");
            }
            var slug = UsernameGenerator.Slugify(req.Username);
            // Slugify caps at MaxLength (≤ 24 chars, under the 30-char DB column), so the only
            // length invariant to check is the fallback escape hatch for unusable input.
            // Accept literal "player" from the client; reject other input that decays to it.
            if (slug == UsernameGenerator.Fallback && !req.Username.Equals(UsernameGenerator.Fallback, StringComparison.OrdinalIgnoreCase))
            {
                return ProblemResults.BadRequest("invalid_username");
            }
            if (slug != user.Username)
            {
                var taken = await db.Users.AnyAsync(u => u.Id != userId && u.Username == slug, ct);
                if (taken)
                {
                    return ProblemResults.Conflict("username_taken");
                }
                user.Username = slug;
                changed = true;
            }
        }

        // Stored verbatim as plain text — newlines and any markup characters included. The web
        // profile renders a small markdown subset from it (lib/richText.ts) by building elements,
        // never HTML, so nothing here is sanitised. Any future consumer that puts a bio into
        // markup owes it the encoding.
        if (req.Bio is not null)
        {
            if (req.Bio.Length > MaxBioLength)
            {
                return ProblemResults.BadRequest("bio_too_long");
            }
            var newBio = req.Bio.Length == 0 ? null : req.Bio;
            if (user.Bio != newBio)
            {
                user.Bio = newBio;
                changed = true;
            }
        }

        if (req.AccentColor is not null)
        {
            var (ok, normalized) = ValidateAccentColor(req.AccentColor);
            if (!ok)
            {
                return ProblemResults.BadRequest("invalid_accent_color");
            }
            if (user.AccentColor != normalized)
            {
                user.AccentColor = normalized;
                changed = true;
            }
        }

        if (req.SocialLinks is not null)
        {
            var (ok, normalized) = ValidateSocialLinks(req.SocialLinks);
            if (!ok)
            {
                return ProblemResults.BadRequest("invalid_social_links");
            }
            // Compare structurally — assigning a "no-op" SocialLinks object would still mark
            // the entity dirty otherwise.
            if (!SocialLinksEqual(user.SocialLinks, normalized))
            {
                user.SocialLinks = normalized;
                changed = true;
            }
        }

        if (!changed)
        {
            return Results.Ok(user.ToMe());
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUsernameUniqueViolation(ex))
        {
            // A concurrent writer took the username between our AnyAsync check and the save.
            return ProblemResults.Conflict("username_taken");
        }
        return Results.Ok(user.ToMe());
    }

    internal static (bool ok, string? value) ValidateAccentColor(string raw)
    {
        // Empty string clears; null arrives only when the field is absent (no-op upstream).
        if (raw.Length == 0)
        {
            return (true, null);
        }
        return AccentColorRegex().IsMatch(raw) ? (true, raw) : (false, null);
    }

    // Per-handle validator + clearing semantics: an empty string for a platform clears that
    // platform; if every platform ends up cleared, the whole SocialLinks object collapses to
    // null so the row carries no jsonb payload rather than `{}`.
    internal static (bool ok, SocialLinks? value) ValidateSocialLinks(SocialLinksDto dto)
    {
        var twitch = NormalizeHandle(dto.Twitch);
        if (twitch.invalid) return (false, null);
        var youtube = NormalizeHandle(dto.YouTube);
        if (youtube.invalid) return (false, null);
        var twitter = NormalizeHandle(dto.Twitter);
        if (twitter.invalid) return (false, null);

        if (twitch.value is null && youtube.value is null && twitter.value is null)
        {
            return (true, null);
        }
        return (true, new SocialLinks
        {
            Twitch = twitch.value,
            YouTube = youtube.value,
            Twitter = twitter.value,
        });
    }

    private static (string? value, bool invalid) NormalizeHandle(string? raw)
    {
        if (raw is null)
        {
            return (null, false);
        }
        // Tolerate the leading "@" users naturally type — strip it before validating so a
        // pasted "@TwitchUser" round-trips as "TwitchUser".
        var trimmed = raw.Trim().TrimStart('@');
        if (trimmed.Length == 0)
        {
            // Empty input clears that platform.
            return (null, false);
        }
        if (trimmed.Length > MaxSocialHandleLength || !SocialHandleRegex().IsMatch(trimmed))
        {
            return (null, true);
        }
        return (trimmed, false);
    }

    private static bool SocialLinksEqual(SocialLinks? a, SocialLinks? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Twitch == b.Twitch && a.YouTube == b.YouTube && a.Twitter == b.Twitter;
    }

    private static bool IsUsernameUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(pg.ConstraintName, "idx_users_username", StringComparison.Ordinal);
}
