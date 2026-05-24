using System.Diagnostics;
using GankedTV.Api.Clips;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.Media.Import;
using GankedTV.Api.Services.Tags;
using GankedTV.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Services.Clips;

// Mirrors ClipUploadService.CreateAsync — same validation cascade (visibility → game → tags),
// same share-code generator, same ClipKeys.BuildVideoKey — but inserts the row directly in
// 'importing' status with the source URL stashed. The ImportWorker picks it up from there.
public sealed class ClipImportService : IClipImportService
{
    private readonly GankedTvDbContext _db;
    private readonly IClipImportUrlValidator _urlValidator;
    private readonly ITagsResolver _tagsResolver;
    private readonly IClipImportSource _source;
    private readonly ClipValidationOptions _validation;
    private readonly IOptionsMonitor<MediaJobOptions> _mediaOptions;
    private readonly TimeProvider _clock;

    public ClipImportService(
        GankedTvDbContext db,
        IClipImportUrlValidator urlValidator,
        ITagsResolver tagsResolver,
        IClipImportSource source,
        IOptions<ClipValidationOptions> validation,
        IOptionsMonitor<MediaJobOptions> mediaOptions,
        TimeProvider clock)
    {
        _db = db;
        _urlValidator = urlValidator;
        _tagsResolver = tagsResolver;
        _source = source;
        _validation = validation.Value;
        _mediaOptions = mediaOptions;
        _clock = clock;
    }

    public async Task<ClipResult<ImportClipPreviewResult>> PreviewAsync(string? url, CancellationToken ct)
    {
        if (!_mediaOptions.CurrentValue.Import.Enabled)
        {
            return ClipResult<ImportClipPreviewResult>.Fail(ClipUploadError.ImportDisabled);
        }
        if (!_urlValidator.TryParse(url, out var normalisedUrl, out var urlError))
        {
            return ClipResult<ImportClipPreviewResult>.Fail(urlError switch
            {
                ImportUrlValidationError.InvalidUrl => ClipUploadError.InvalidUrl,
                ImportUrlValidationError.UnsupportedHost => ClipUploadError.UnsupportedHost,
                _ => ClipUploadError.InvalidUrl,
            });
        }

        try
        {
            var media = await _source.ProbeAsync(normalisedUrl, ct);
            return ClipResult<ImportClipPreviewResult>.Ok(new ImportClipPreviewResult(
                Title: media.Title,
                DurationSecs: media.DurationSecs,
                Width: media.Width,
                Height: media.Height,
                ThumbnailUrl: media.ThumbnailUrl,
                MaxClipDurationSecs: _validation.MaxClipDurationSecs));
        }
        catch (ImportSourceRejectedException)
        {
            // Reuse UnsupportedHost? No — we want a distinct "source unavailable" surface.
            // Map onto a new error code below (private/geo-blocked/removed).
            return ClipResult<ImportClipPreviewResult>.Fail(ClipUploadError.SourceUnavailable);
        }
        catch (ImportFetchException)
        {
            return ClipResult<ImportClipPreviewResult>.Fail(ClipUploadError.FetchFailed);
        }
    }

    public async Task<ClipResult<ImportClipResult>> SubmitAsync(
        Guid userId,
        ImportClipInput input,
        CancellationToken ct)
    {
        // Pipeline-level kill switch — covers prod incidents (e.g. yt-dlp host outage) without
        // having to ship code. Mirrors how /clips would 503 if the upload pipeline were down.
        if (!_mediaOptions.CurrentValue.Import.Enabled)
        {
            return ClipResult<ImportClipResult>.Fail(ClipUploadError.ImportDisabled);
        }

        if (!_urlValidator.TryParse(input.Url, out var normalisedUrl, out var urlError))
        {
            return ClipResult<ImportClipResult>.Fail(urlError switch
            {
                ImportUrlValidationError.InvalidUrl => ClipUploadError.InvalidUrl,
                ImportUrlValidationError.UnsupportedHost => ClipUploadError.UnsupportedHost,
                _ => ClipUploadError.InvalidUrl,
            });
        }

        // Title is optional for imports — when omitted, the worker fills it from the extractor's
        // metadata. When provided, it goes through the same length cap as direct uploads.
        var trimmedTitle = input.Title?.Trim();
        var title = string.IsNullOrEmpty(trimmedTitle) ? ClipImportDefaults.PlaceholderTitle : trimmedTitle;
        if (title.Length > _validation.MaxTitleLength)
        {
            return ClipResult<ImportClipResult>.Fail(ClipUploadError.InvalidTitle);
        }

        if (input.Description is { Length: var descLen } && descLen > _validation.MaxDescriptionLength)
        {
            return ClipResult<ImportClipResult>.Fail(ClipUploadError.InvalidDescription);
        }

        var rawVisibility = input.Visibility ?? ClipVisibilities.Public;
        if (!ClipVisibilities.IsValid(rawVisibility))
        {
            return ClipResult<ImportClipResult>.Fail(ClipUploadError.InvalidVisibility);
        }
        var visibility = ClipVisibilities.Normalize(rawVisibility);

        string? gameSlug = null;
        if (input.GameId is { } gameId)
        {
            gameSlug = await _db.Games.AsNoTracking()
                .Where(g => g.Id == gameId)
                .Select(g => g.Slug)
                .FirstOrDefaultAsync(ct);
            if (gameSlug is null)
            {
                return ClipResult<ImportClipResult>.Fail(ClipUploadError.InvalidGame);
            }
        }

        var requestedTags = input.Tags ?? [];
        var tagsResult = await _tagsResolver.ResolveAsync(requestedTags, ct);
        if (!tagsResult.IsSuccess)
        {
            return ClipResult<ImportClipResult>.Fail(MapTagsError(tagsResult.Error!.Value));
        }

        var id = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        var shareCode = await ShareCodeGenerator.GenerateUniqueAsync(_db.Clips, ct);
        var clip = new Clip
        {
            Id = id,
            UserId = userId,
            GameId = input.GameId,
            Title = title,
            Description = string.IsNullOrEmpty(input.Description) ? null : input.Description,
            VideoKey = ClipKeys.BuildVideoKey(userId, id, gameSlug),
            ShareCode = shareCode,
            Status = ClipStatuses.Importing,
            Visibility = visibility,
            ImportSourceUrl = normalisedUrl,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _tagsResolver.SetClipTags(clip, tagsResult.Tags);
        _db.Clips.Add(clip);
        await _db.SaveChangesAsync(ct);

        return ClipResult<ImportClipResult>.Ok(new ImportClipResult(id, ClipStatuses.Importing));
    }

    // Same mapping as ClipUploadService.MapTagsError. Duplicated rather than exposed because
    // both services exhaustively map the same enum and the mirroring keeps each service's
    // error space self-contained.
    private static ClipUploadError MapTagsError(TagsResolveError error) => error switch
    {
        TagsResolveError.TooManyTags => ClipUploadError.TooManyTags,
        TagsResolveError.InvalidTag => ClipUploadError.InvalidTag,
        _ => throw new UnreachableException($"Unmapped TagsResolveError: {error}"),
    };
}
