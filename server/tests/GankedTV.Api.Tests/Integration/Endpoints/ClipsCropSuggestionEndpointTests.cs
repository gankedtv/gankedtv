using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Media;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

// GET /clips/{id}/crop-suggestion. The detection itself is unit-tested in CropDetectServiceTests;
// this covers the endpoint's own contract — auth, ownership, which statuses are allowed, the
// kill switch, and that a detection failure degrades rather than 5xx-ing.
[Collection("PostgresClips")]
public class ClipsCropSuggestionEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;
    private ICropDetectService _cropDetect = null!;

    public ClipsCropSuggestionEndpointTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _storage = Substitute.For<IObjectStorageService>();
        _storage.GetPresignedGetUrlForWorker(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://minio.local/presigned");
        _cropDetect = Substitute.For<ICropDetectService>();
        _cropDetect.DetectAsync(Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(new CropSuggestion(true, new CropRect(0.1279, 0, 0.7442, 1), 3440, 1440, 3));
        _factory = NewFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private AuthApiFactory NewFactory(Action<MediaJobOptions>? configureMedia = null) =>
        new(_fx.ConnectionString, _storage, configureServices: services =>
        {
            services.AddScoped(_ => _cropDetect);
            if (configureMedia is not null) services.Configure(configureMedia);
        });

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "cropper") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private async Task<Guid> SeedClipAsync(
        Guid userId,
        string status = ClipStatuses.Ready,
        string visibility = ClipVisibilities.Public)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = "ultrawide capture",
            VideoKey = $"{userId}/{id}.mp4",
            ThumbnailKey = status == ClipStatuses.Ready ? $"thumbs/{id}.jpg" : null,
            ShareCode = ShareCodeGenerator.Next(),
            Status = status,
            Visibility = visibility,
            DurationSecs = 30,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Detected_ReturnsRectAndSourceDimensions()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await client.GetAsync($"/clips/{clipId}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detected").GetBoolean().Should().BeTrue();
        body.GetProperty("sourceWidth").GetInt32().Should().Be(3440);
        body.GetProperty("sourceHeight").GetInt32().Should().Be(1440);
        body.GetProperty("samples").GetInt32().Should().Be(3);
        var crop = body.GetProperty("crop");
        crop.GetProperty("x").GetDouble().Should().BeApproximately(0.1279, 1e-9);
        crop.GetProperty("width").GetDouble().Should().BeApproximately(0.7442, 1e-9);
    }

    [Fact]
    public async Task AllowedOnDraft_SoTheUploadWizardCanOfferItBeforePublish()
    {
        // At draft time the raw upload is the only thing in storage, and that's exactly when the
        // user is standing in the crop editor.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, status: ClipStatuses.Draft);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await client.GetAsync($"/clips/{clipId}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(ClipStatuses.Processing)]
    [InlineData(ClipStatuses.Transcoding)]
    [InlineData(ClipStatuses.Failed)]
    public async Task MidPipelineStatuses_Return404(string status)
    {
        // The master is being rewritten; a rect measured against it would be measured against
        // something that no longer exists by the time the user applies it.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, status: status);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await client.GetAsync($"/clips/{clipId}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NotDetected_Returns200WithNullCrop()
    {
        // A miss is not an error: the client hides the "Remove black bars" button and the manual
        // cropper still works.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        _cropDetect.DetectAsync(Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(new CropSuggestion(false, null, 1920, 1080, 3));

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await client.GetAsync($"/clips/{clipId}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detected").GetBoolean().Should().BeFalse();
        body.GetProperty("crop").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task NonOwner_Returns404()
    {
        // 404 rather than 403: the endpoint is owner-scoped, and confirming a clip exists to a
        // stranger who can't use the answer buys nothing.
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("owner");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("intruder");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, otherToken);
        var resp = await client.GetAsync($"/clips/{clipId}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HiddenClip_Returns404()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, visibility: ClipVisibilities.Hidden);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await client.GetAsync($"/clips/{clipId}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync($"/clips/{Guid.NewGuid()}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ClipMissing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await client.GetAsync($"/clips/{Guid.NewGuid()}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CropDetectDisabled_Returns503WithoutForkingFfmpeg()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        await using var factory = NewFactory(o => o.CropDetectEnabled = false);
        using var client = AuthTestHelpers.CreateBearerClient(factory, token);
        var resp = await client.GetAsync($"/clips/{clipId}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("crop_detect_unavailable");
        await _cropDetect.DidNotReceive().DetectAsync(
            Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectionThrowsCancelled_DegradesTo200NotDetected()
    {
        // Budget blown while the caller is still connected. The user is waiting inside the crop
        // editor — degrade to "no suggestion" rather than surfacing an error.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        _cropDetect.DetectAsync(Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns<CropSuggestion>(_ => throw new OperationCanceledException());

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        var resp = await client.GetAsync($"/clips/{clipId}/crop-suggestion");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PassesTheClipDurationSoSamplesSpreadAcrossIt()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, token);
        (await client.GetAsync($"/clips/{clipId}/crop-suggestion")).EnsureSuccessStatusCode();

        await _cropDetect.Received(1).DetectAsync(
            Arg.Any<string>(), Arg.Is<double?>(d => d == 30), Arg.Any<CancellationToken>());
    }
}
