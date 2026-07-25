using System.Linq.Expressions;
using System.Net;
using System.Security.Claims;
using GankedTV.Api.Auth;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.ObjectStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Endpoints;

// Everything a link-preview crawler or embed client touches when a ganked.tv URL is posted
// somewhere: crawler-facing OG HTML for /c/{code} and /clip/{id}, the oEmbed document, and
// stable share-media URLs that 302 to a freshly presigned object URL. The web edge (Caddy)
// proxies crawler traffic on the share paths here — see web/Caddyfile.
public static class SharePreviewEndpoints
{
    private const string SiteName = "GankedTV";
    // --color-accent from the web design system; Discord uses it as the embed accent color.
    private const string ThemeColor = "#00e5a0";
    // Set by the web edge when it proxies a request it classified as crawler traffic.
    // Trusted in addition to the UA list so a UA-list mismatch between edge and API degrades
    // to serving OG HTML rather than bouncing the crawler through a redirect loop. Spoofing
    // it only yields the same public OG HTML, so no trust boundary is crossed.
    private const string EdgePreviewHeader = "X-GankedTV-Share-Preview";

    private static readonly TimeSpan MediaUrlLifetime = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapSharePreviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/c/{code:length(6,12)}", GetByShareCode);
        // Stable media URLs referenced from the OG HTML and oEmbed payloads: embed clients
        // fetch media long after a presigned URL embedded directly would have expired, so
        // each fetch re-signs. Cheap: one indexed row lookup + local signing, no S3 round trip.
        app.MapGet("/c/{code:length(6,12)}/poster.jpg", GetPoster);
        app.MapGet("/c/{code:length(6,12)}/video.mp4", GetVideo);
        app.MapGet("/clip/{id:guid}", GetClipPreview);
        app.MapGet("/oembed", GetOEmbed);
        return app;
    }

    private static async Task<IResult> GetByShareCode(
        string code,
        HttpRequest request,
        IOptions<OAuthOptions> oauthOptions,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        // Private and hidden clips resolve for the owner only — crawlers and anyone else
        // holding the share link get the same 404 as a nonexistent code.
        var viewerId = principal.GetUserIdOrNull();
        var result = await ClipsReadEndpoints.LoadClipWithUrlsAsync(
            c => c.ShareCode == code && c.Status == ClipStatuses.Ready,
            viewerId, db, storage, s3, ct);

        if (result is null)
            return ProblemResults.NotFound("not_found");

        var (clip, videoUrl, thumbnailUrl) = result.Value;
        var webOrigin = oauthOptions.Value.WebOrigin.TrimEnd('/');

        // Crawler UA wins over Accept negotiation: a bot advertising application/json
        // (rare but possible) still needs the OG HTML so previews render.
        if (IsPreviewFetch(request))
            return Results.Content(BuildOgHtml(clip, webOrigin), "text/html; charset=utf-8");

        if (request.Headers.Accept.ToString().Contains("application/json"))
            return await ClipsReadEndpoints.BuildDetailResultAsync(clip, videoUrl, thumbnailUrl, principal, db, ct);

        return Results.Redirect($"{webOrigin}/c/{code}", permanent: false);
    }

    // The /clip/{id} web route is what users copy from the address bar, so it must preview
    // as well as the share-code URL. Humans landing here (direct API-origin hits) bounce to
    // the web app; there is no JSON branch — /clips/{id} is the API surface for that.
    private static async Task<IResult> GetClipPreview(
        Guid id,
        HttpRequest request,
        IOptions<OAuthOptions> oauthOptions,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var viewerId = principal.GetUserIdOrNull();
        var webOrigin = oauthOptions.Value.WebOrigin.TrimEnd('/');

        if (!IsPreviewFetch(request))
            return Results.Redirect($"{webOrigin}/clip/{id}", permanent: false);

        var result = await ClipsReadEndpoints.LoadClipWithUrlsAsync(
            c => c.Id == id && c.Status == ClipStatuses.Ready,
            viewerId, db, storage, s3, ct);

        if (result is null)
            return ProblemResults.NotFound("not_found");

        return Results.Content(BuildOgHtml(result.Value.clip, webOrigin), "text/html; charset=utf-8");
    }

    private static async Task<IResult> GetPoster(
        string code,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var clip = await FindVisibleReadyClipAsync(db, principal, c => c.ShareCode == code, ct);
        if (clip is null)
            return ProblemResults.NotFound("not_found");

        var url = ClipsReadEndpoints.BuildThumbnailUrl(storage, s3.Value.ThumbnailsBucket, clip.ThumbnailKey);
        return Results.Redirect(url, permanent: false);
    }

    private static async Task<IResult> GetVideo(
        string code,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        CancellationToken ct)
    {
        var clip = await FindVisibleReadyClipAsync(db, principal, c => c.ShareCode == code, ct);
        if (clip is null)
            return ProblemResults.NotFound("not_found");

        var url = storage.GetPresignedGetUrl(s3.Value.ClipsBucket, clip.VideoKey, MediaUrlLifetime);
        return Results.Redirect(url, permanent: false);
    }

    private static async Task<IResult> GetOEmbed(
        string? url,
        string? format,
        IOptions<OAuthOptions> oauthOptions,
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        CancellationToken ct)
    {
        // Per the oEmbed spec, a format the provider doesn't implement is 501 (json only here).
        if (format is not null && !format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        if (string.IsNullOrWhiteSpace(url))
            return ProblemResults.BadRequest("missing_url");

        var webOrigin = oauthOptions.Value.WebOrigin.TrimEnd('/');
        var predicate = ParseShareUrl(url, webOrigin);
        if (predicate is null)
            return ProblemResults.NotFound("not_found");

        var viewerId = principal.GetUserIdOrNull();
        var clip = await db.Clips.AsNoTracking()
            .Include(c => c.User)
            .WhereVisibleTo(viewerId)
            .Where(c => c.Status == ClipStatuses.Ready)
            .FirstOrDefaultAsync(predicate, ct);

        if (clip is null)
            return ProblemResults.NotFound("not_found");

        // `link` rather than `video`: the spec requires an embeddable iframe document for
        // video-type responses, which doesn't exist (yet). Consumers that want playback use
        // the og:video tags; oEmbed contributes title + author/provider attribution.
        return Results.Ok(new
        {
            version = "1.0",
            type = "link",
            title = clip.Title,
            author_name = clip.User.Username,
            author_url = $"{webOrigin}/user/{Uri.EscapeDataString(clip.User.Username)}",
            provider_name = SiteName,
            provider_url = webOrigin,
            thumbnail_url = $"{webOrigin}/c/{clip.ShareCode}/poster.jpg",
            thumbnail_width = clip.Width,
            thumbnail_height = clip.Height,
        });
    }

    // Accepts the two shareable web URL shapes — {webOrigin}/c/{code} and {webOrigin}/clip/{id}
    // — and returns the matching clip predicate, or null when the URL is foreign or unshaped.
    private static Expression<Func<Clip, bool>>? ParseShareUrl(string url, string webOrigin)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || !Uri.TryCreate(webOrigin, UriKind.Absolute, out var origin)
            || !string.Equals(
                target.GetLeftPart(UriPartial.Authority),
                origin.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = target.AbsolutePath.Trim('/').Split('/');
        return segments switch
        {
            ["c", var code] when code.Length is >= 6 and <= 12 => c => c.ShareCode == code,
            ["clip", var rawId] when Guid.TryParse(rawId, out var id) => BuildIdPredicate(rawId),
            _ => null,
        };
    }

    // Guid.TryParse's out var isn't usable inside the switch-arm expression tree, so re-parse.
    private static Expression<Func<Clip, bool>> BuildIdPredicate(string rawId)
    {
        var id = Guid.Parse(rawId);
        return c => c.Id == id;
    }

    private static Task<Clip?> FindVisibleReadyClipAsync(
        GankedTvDbContext db,
        ClaimsPrincipal principal,
        Expression<Func<Clip, bool>> predicate,
        CancellationToken ct)
        => db.Clips.AsNoTracking()
            .WhereVisibleTo(principal.GetUserIdOrNull())
            .Where(c => c.Status == ClipStatuses.Ready)
            .FirstOrDefaultAsync(predicate, ct);

    private static readonly string[] CrawlerSubstrings =
    [
        "Discordbot", "Twitterbot", "facebookexternalhit", "Slackbot",
        "LinkedInBot", "TelegramBot", "WhatsApp", "redditbot"
    ];

    // Keep the list in sync with the crawler matcher in web/Caddyfile; the edge header below
    // covers any drift.
    private static bool IsCrawler(string userAgent) =>
        CrawlerSubstrings.Any(s => userAgent.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static bool IsPreviewFetch(HttpRequest request) =>
        request.Headers.ContainsKey(EdgePreviewHeader)
        || IsCrawler(request.Headers.UserAgent.ToString());

    private static string BuildOgHtml(Clip clip, string webOrigin)
    {
        var title = WebUtility.HtmlEncode(clip.Title);
        // IsNullOrWhiteSpace, not just null: an empty Description still emits an empty
        // <meta og:description> which crawlers (notably Slack) render as a blank line.
        var desc = !string.IsNullOrWhiteSpace(clip.Description) ? WebUtility.HtmlEncode(clip.Description) : null;
        // webOrigin is the public, config-driven origin (not request.Scheme/Host, which
        // behind a reverse proxy without UseForwardedHeaders surfaces the internal URL).
        var canonicalUrl = WebUtility.HtmlEncode($"{webOrigin}/c/{clip.ShareCode}");
        // Media URLs are the stable share paths — a fresh presign per fetch — so embeds keep
        // playing long after a directly-presigned URL would have expired.
        var posterUrl = WebUtility.HtmlEncode($"{webOrigin}/c/{clip.ShareCode}/poster.jpg");
        var videoUrl = WebUtility.HtmlEncode($"{webOrigin}/c/{clip.ShareCode}/video.mp4");
        var oembedUrl = WebUtility.HtmlEncode(
            $"{webOrigin}/oembed?url={Uri.EscapeDataString($"{webOrigin}/c/{clip.ShareCode}")}&format=json");
        // width/height fallback: Discord/Twitter require numeric values
        var width = clip.Width?.ToString() ?? "1280";
        var height = clip.Height?.ToString() ?? "720";
        // video/mp4 is hardcoded — it's the only content type allowed by ClipValidationOptions

        // Twitter card: summary_large_image rather than `player`. `player` requires an
        // HTTPS iframe HTML page (not a raw mp4) plus Twitter app approval; pointing
        // it at the presigned mp4 falls back to summary anyway. summary_large_image
        // renders the thumbnail + title/description directly.
        return $"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8" />
            <title>{title}</title>
            <meta name="theme-color" content="{ThemeColor}" />
            <link rel="alternate" type="application/json+oembed" href="{oembedUrl}" title="{title}" />
            <meta property="og:site_name" content="{SiteName}" />
            <meta property="og:type" content="video.other" />
            <meta property="og:url" content="{canonicalUrl}" />
            <meta property="og:title" content="{title}" />
            {(desc is not null ? $"""<meta property="og:description" content="{desc}" />""" : "")}
            <meta property="og:image" content="{posterUrl}" />
            <meta property="og:image:type" content="image/jpeg" />
            <meta property="og:image:width" content="{width}" />
            <meta property="og:image:height" content="{height}" />
            <meta property="og:video" content="{videoUrl}" />
            <meta property="og:video:secure_url" content="{videoUrl}" />
            <meta property="og:video:type" content="video/mp4" />
            <meta property="og:video:width" content="{width}" />
            <meta property="og:video:height" content="{height}" />
            <meta name="twitter:card" content="summary_large_image" />
            <meta name="twitter:title" content="{title}" />
            {(desc is not null ? $"""<meta name="twitter:description" content="{desc}" />""" : "")}
            <meta name="twitter:image" content="{posterUrl}" />
            </head>
            <body></body>
            </html>
            """;
    }
}
