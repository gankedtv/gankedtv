using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Clips;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.Tags;
using GankedTV.Api.Tests.TestSupport;
using GankedTV.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Services.Clips;

[Collection("Postgres")]
public class ClipImportServiceTests
{
    private readonly PostgresFixture _fx;

    public ClipImportServiceTests(PostgresFixture fx) => _fx = fx;

    private async Task<(ClipImportService svc, GankedTvDbContext db, GankedTV.Api.Services.Media.Import.IClipImportSource source, Guid userId)> BuildAsync(
        bool importEnabled = true)
    {
        await _fx.ResetAsync();
        var db = _fx.CreateContext();

        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = "import-user",
            Email = "import-user@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var validator = new ClipImportUrlValidator(MonitorWith(new MediaJobOptions()));
        var tagsResolver = new TagsResolver(db, TimeProvider.System);
        var mediaOpts = MonitorWith(new MediaJobOptions { Import = new ImportOptions { Enabled = importEnabled } });
        // SubmitAsync doesn't touch the import source, so a stub is fine here. PreviewAsync
        // tests below explicitly configure ProbeAsync on the substitute when needed.
        var source = Substitute.For<GankedTV.Api.Services.Media.Import.IClipImportSource>();

        var svc = new ClipImportService(
            db,
            validator,
            tagsResolver,
            source,
            Microsoft.Extensions.Options.Options.Create(new ClipValidationOptions()),
            mediaOpts,
            TimeProvider.System);
        return (svc, db, source, userId);
    }

    private static IOptionsMonitor<MediaJobOptions> MonitorWith(MediaJobOptions options)
    {
        var m = Substitute.For<IOptionsMonitor<MediaJobOptions>>();
        m.CurrentValue.Returns(options);
        return m;
    }

    [Fact]
    public async Task Submit_AllowedUrl_InsertsImportingRow()
    {
        var (svc, db, _, userId) = await BuildAsync();

        var result = await svc.SubmitAsync(
            userId,
            new ImportClipInput("https://medal.tv/clips/abc", "epic", null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == result.Value!.ClipId);
        clip.Status.Should().Be(ClipStatuses.Importing);
        clip.Title.Should().Be("epic");
        clip.ImportSourceUrl.Should().Be("https://medal.tv/clips/abc");
        clip.ShareCode.Should().NotBeNullOrEmpty();
        clip.VideoKey.Should().Be($"{userId}/{clip.Id}.mp4");
    }

    [Fact]
    public async Task Submit_NoTitle_UsesPlaceholder()
    {
        var (svc, db, _, userId) = await BuildAsync();

        var result = await svc.SubmitAsync(
            userId,
            new ImportClipInput("https://medal.tv/x", null, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == result.Value!.ClipId);
        clip.Title.Should().Be(ClipImportDefaults.PlaceholderTitle);
    }

    [Fact]
    public async Task Submit_UnsupportedHost_ReturnsUnsupportedHostError()
    {
        var (svc, _, _, userId) = await BuildAsync();

        var result = await svc.SubmitAsync(
            userId,
            new ImportClipInput("https://vimeo.com/x", null, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ClipUploadError.UnsupportedHost);
    }

    [Fact]
    public async Task Submit_InvalidUrl_ReturnsInvalidUrlError()
    {
        var (svc, _, _, userId) = await BuildAsync();

        var result = await svc.SubmitAsync(
            userId,
            new ImportClipInput("not-a-url", null, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ClipUploadError.InvalidUrl);
    }

    [Fact]
    public async Task Submit_ImportDisabled_ReturnsImportDisabled()
    {
        var (svc, _, _, userId) = await BuildAsync(importEnabled: false);

        var result = await svc.SubmitAsync(
            userId,
            new ImportClipInput("https://medal.tv/x", null, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ClipUploadError.ImportDisabled);
    }

    [Fact]
    public async Task Submit_InvalidVisibility_Returns400()
    {
        var (svc, _, _, userId) = await BuildAsync();

        var result = await svc.SubmitAsync(
            userId,
            new ImportClipInput("https://medal.tv/x", "title", null, null, "secret", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ClipUploadError.InvalidVisibility);
    }

    [Fact]
    public async Task Submit_UnknownGameId_ReturnsInvalidGame()
    {
        var (svc, _, _, userId) = await BuildAsync();

        var result = await svc.SubmitAsync(
            userId,
            new ImportClipInput("https://medal.tv/x", "title", null, 99_999, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ClipUploadError.InvalidGame);
    }

    [Fact]
    public async Task Preview_AllowedUrl_ReturnsMetadataAndCap()
    {
        var (svc, _, source, _) = await BuildAsync();
        source.ProbeAsync("https://www.youtube.com/watch?v=abc", Arg.Any<CancellationToken>())
            .Returns(new GankedTV.Api.Services.Media.Import.ImportedMedia(
                "My Clip", 42, 1280, 720, "https://i.ytimg.com/vi/abc/maxresdefault.jpg"));

        var result = await svc.PreviewAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Title.Should().Be("My Clip");
        result.Value.DurationSecs.Should().Be(42);
        result.Value.MaxClipDurationSecs.Should().Be(120);
        // The platform-resolved thumbnail must flow through — the wizard uses it as the
        // preview poster for sources where the client-side YouTube fallback can't help
        // (Medal.tv etc.).
        result.Value.ThumbnailUrl.Should().Be("https://i.ytimg.com/vi/abc/maxresdefault.jpg");
    }

    [Fact]
    public async Task Preview_UnsupportedHost_DoesNotCallSource()
    {
        var (svc, _, source, _) = await BuildAsync();

        var result = await svc.PreviewAsync("https://vimeo.com/x", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ClipUploadError.UnsupportedHost);
        await source.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_ImportDisabled_ReturnsImportDisabled()
    {
        var (svc, _, _, _) = await BuildAsync(importEnabled: false);

        var result = await svc.PreviewAsync("https://medal.tv/x", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ClipUploadError.ImportDisabled);
    }

    [Fact]
    public async Task Preview_SourceRejected_ReturnsSourceUnavailable()
    {
        var (svc, _, source, _) = await BuildAsync();
        source.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new GankedTV.Api.Services.Media.Import.ImportSourceRejectedException(
                ClipFailureReasons.SourceUnavailable, "private"));

        var result = await svc.PreviewAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ClipUploadError.SourceUnavailable);
    }
}
