using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresClips")]
public class ClipsUploadEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ClipsUploadEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _storage = Substitute.For<IObjectStorageService>();
        _factory = new AuthApiFactory(_fx.ConnectionString, _storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "uploader") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<Guid> SeedClipAsync(Guid userId, string status = "draft", long? fileSizeBytes = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = "seed",
            VideoKey = $"{userId}/{id}.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = status,
            Visibility = "public",
            FileSizeBytes = fileSizeBytes,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- POST /clips ----

    [Fact]
    public async Task Create_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/clips", new { title = "test" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_ValidPayload_Returns200AndPersistsDraft()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new
        {
            title = "My first clip",
            description = "did a thing",
            visibility = "unlisted",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == id);
        clip.UserId.Should().Be(userId);
        clip.Status.Should().Be("draft");
        clip.VideoKey.Should().Be($"{userId}/{id}.mp4");
        clip.Visibility.Should().Be("unlisted");
        clip.Title.Should().Be("My first clip");
        clip.Description.Should().Be("did a thing");
    }

    [Fact]
    public async Task Create_DefaultsVisibilityToPublic()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new { title = "no vis" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == id);
        clip.Visibility.Should().Be("public");
    }

    [Fact]
    public async Task Create_WhitespaceTitle_Returns400()
    {
        // [Required] (default AllowEmptyStrings=false) rejects whitespace-only before it
        // reaches the service layer → ValidationProblemDetails keyed by "Title".
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new { title = "   " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("Title").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Create_TitleTooLong_Returns400()
    {
        // Caught by the [StringLength] attribute in CreateClipRequest → ValidationProblemDetails.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new { title = new string('x', 256) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("Title").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("hidden")] // moderation-owned, never user-settable
    [InlineData("friends")] // unknown value
    public async Task Create_InvalidVisibility_Returns400(string visibility)
    {
        // Visibility validation lives in ClipUploadService (case-insensitive allowed-values)
        // rather than a DataAnnotation, so this surfaces via ProblemResults with code=invalid_visibility.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new { title = "x", visibility });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_visibility");
    }

    [Fact]
    public async Task Create_PrivateVisibility_Persists()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new { title = "just for me", visibility = "private" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == id);
        clip.Visibility.Should().Be("private");
    }

    [Fact]
    public async Task Create_MixedCaseVisibility_IsNormalized()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new
        {
            title = "vis test",
            visibility = "Unlisted",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == id);
        clip.Visibility.Should().Be("unlisted");
    }

    [Fact]
    public async Task Create_WithValidGameId_PersistsKeyWithSlugSegment()
    {
        // Look up the seeded "valorant" by slug rather than hard-coding the id —
        // a future reorder of the HasData seed shouldn't quietly break this test.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();

        int valorantId;
        await using (var lookup = _fx.CreateContext())
        {
            valorantId = await lookup.Games
                .Where(g => g.Slug == "valorant")
                .Select(g => g.Id)
                .SingleAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync("/clips", new { title = "ace", gameId = valorantId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == id);
        clip.GameId.Should().Be(valorantId);
        clip.VideoKey.Should().Be($"{userId}/valorant/{id}.mp4");
    }

    [Fact]
    public async Task Create_WithUnknownGameId_Returns400InvalidGame()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new { title = "x", gameId = 999_999 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_game");
    }

    [Fact]
    public async Task Create_NullBody_Returns400()
    {
        // ValidationEndpointFilter<CreateClipRequest> catches a null-deserialized body and
        // short-circuits with a ValidationProblemDetails keyed by "body".
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        using var content = new StringContent("null", System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/clips", content);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("body").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Create_DescriptionTooLong_Returns400()
    {
        // Caught by the [StringLength] attribute on CreateClipRequest.Description.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new
        {
            title = "ok",
            description = new string('x', 5001),
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("Description").GetArrayLength().Should().BeGreaterThan(0);
    }

    // ---- POST /clips/{id}/upload-url ----

    [Fact]
    public async Task UploadUrl_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsync($"/clips/{Guid.NewGuid()}/upload-url", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UploadUrl_NotOwned_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("me");
        var (otherId, _) = await SeedUserAndIssueTokenAsync("other");
        var otherClip = await SeedClipAsync(otherId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{otherClip}/upload-url", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("not_found");
    }

    [Fact]
    public async Task UploadUrl_NonExistent_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync($"/clips/{Guid.NewGuid()}/upload-url", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UploadUrl_ClipAlreadyReady_Returns400InvalidState()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, status: "ready");

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/upload-url", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_state");
    }

    [Fact]
    public async Task UploadUrl_Happy_CallsPresignWithExpectedArgs()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetPresignedPutUrl(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("http://localhost:9000/clips/signed?sig=abc");

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/upload-url", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("url").GetString().Should().Be("http://localhost:9000/clips/signed?sig=abc");
        body.GetProperty("expiresAt").GetDateTimeOffset()
            .Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
        // Pinned to the same content type the server signed for so a future contract
        // change is forced to think about the client-side Content-Type header too.
        body.GetProperty("contentType").GetString().Should().Be("video/mp4");

        _storage.Received(1).GetPresignedPutUrl(
            "clips",
            $"{userId}/{clipId}.mp4",
            "video/mp4",
            TimeSpan.FromMinutes(15));
    }

    // ---- POST /clips/{id}/complete ----

    [Fact]
    public async Task Complete_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsync($"/clips/{Guid.NewGuid()}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Complete_NonExistentId_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync($"/clips/{Guid.NewGuid()}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Complete_NotOwned_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("me");
        var (otherId, _) = await SeedUserAndIssueTokenAsync("other");
        var otherClip = await SeedClipAsync(otherId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{otherClip}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Complete_NotInDraft_Returns400InvalidState()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, status: "ready");

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_state");
    }

    [Fact]
    public async Task Complete_ObjectMissing_Returns400ObjectNotUploaded()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ObjectMetadata?)null);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("object_not_uploaded");
    }

    [Fact]
    public async Task Complete_FileTooLarge_Returns400AndLeavesDraft()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        var oversized = (long)501 * 1024 * 1024;
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(oversized, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("file_too_large");

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.Status.Should().Be("draft");
        clip.FileSizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task Complete_UnsupportedContentType_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/quicktime"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("unsupported_content_type");
    }

    [Fact]
    public async Task Complete_NullContentType_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, null));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("unsupported_content_type");
    }

    [Fact]
    public async Task Complete_ContentTypeMixedCase_IsAccepted()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "VIDEO/MP4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Complete_ContentTypeWithCharset_IsAccepted()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(12345, "video/mp4; charset=binary"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Complete_Happy_UpdatesRowAndReturnsFileSize()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        DateTimeOffset originalUpdated;
        await using (var db = _fx.CreateContext())
        {
            originalUpdated = (await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId)).UpdatedAt;
        }
        await Task.Delay(20);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(98765, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(clipId);
        body.GetProperty("fileSizeBytes").GetInt64().Should().Be(98765);

        await using var db2 = _fx.CreateContext();
        var clip = await db2.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        // /complete now transitions Draft -> Processing; the media-job worker flips it to
        // Ready (or Failed) after extracting the thumbnail. Status=='ready' is asserted by
        // the worker-side tests in Services.Media.
        clip.Status.Should().Be("processing");
        clip.FileSizeBytes.Should().Be(98765);
        clip.UpdatedAt.Should().BeAfter(originalUpdated);
    }

    [Fact]
    public async Task Complete_WithTrim_PersistsTrimRange()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { trimStartSeconds = 1.5, trimEndSeconds = 9.25 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.TrimStartSecs.Should().Be(1.5);
        clip.TrimEndSecs.Should().Be(9.25);
    }

    [Fact]
    public async Task Complete_WithoutBody_LeavesTrimNull()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.TrimStartSecs.Should().BeNull();
        clip.TrimEndSecs.Should().BeNull();
    }

    [Theory]
    [InlineData(-1.0, 5.0)] // negative start
    [InlineData(5.0, 5.1)] // span under the 0.2s minimum
    [InlineData(9.0, 3.0)] // inverted range
    [InlineData(2.0, null)] // half-specified (end missing)
    [InlineData(null, 5.0)] // half-specified (start missing)
    public async Task Complete_InvalidTrim_Returns400(double? start, double? end)
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { trimStartSeconds = start, trimEndSeconds = end });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_trim");

        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId)).Status.Should().Be("draft");
    }

    [Fact]
    public async Task Complete_TrimAtExactMinimumSpan_IsAccepted()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        // 1.7 - 1.5 is 0.19999… in doubles; the guard's epsilon must not reject an
        // exact-minimum span over FP representation.
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { trimStartSeconds = 1.5, trimEndSeconds = 1.7 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Complete_TrimWithTranscodeDisabled_Returns400TrimUnavailable()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        await using var factory = new AuthApiFactory(_fx.ConnectionString, _storage, configureServices: services =>
            services.Configure<GankedTV.Api.Services.Media.MediaJobOptions>(o => o.TranscodeEnabled = false));
        using var client = AuthTestHelpers.CreateBearerClient(factory, token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { trimStartSeconds = 1.0, trimEndSeconds = 5.0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("trim_unavailable");
    }

    // ---- crop ----

    [Fact]
    public async Task Complete_WithCrop_PersistsNormalizedRect()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { cropX = 0.1279, cropY = 0.0, cropWidth = 0.7442, cropHeight = 1.0 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.CropX.Should().BeApproximately(0.1279, 1e-9);
        clip.CropY.Should().Be(0);
        clip.CropWidth.Should().BeApproximately(0.7442, 1e-9);
        clip.CropHeight.Should().Be(1);
        clip.Status.Should().Be("processing");
    }

    [Fact]
    public async Task Complete_WithTrimAndCrop_PersistsBoth()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new
            {
                trimStartSeconds = 1.5,
                trimEndSeconds = 9.25,
                cropX = 0.1,
                cropY = 0.0,
                cropWidth = 0.8,
                cropHeight = 1.0,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.TrimStartSecs.Should().Be(1.5);
        clip.CropWidth.Should().BeApproximately(0.8, 1e-9);
    }

    [Fact]
    public async Task Complete_WithoutBody_LeavesCropNull()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/complete", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId);
        clip.CropX.Should().BeNull();
        clip.CropWidth.Should().BeNull();
    }

    [Theory]
    // Partial rect — no defensible interpretation.
    [InlineData(0.1, null, 0.5, 0.5)]
    [InlineData(null, 0.1, 0.5, 0.5)]
    [InlineData(0.1, 0.1, 0.5, null)]
    // Out of range.
    [InlineData(-0.1, 0.0, 0.5, 0.5)]
    [InlineData(0.0, 0.0, 1.5, 0.5)]
    [InlineData(0.6, 0.0, 0.5, 0.5)]
    // Below the minimum extent.
    [InlineData(0.0, 0.0, 0.5, 0.01)]
    public async Task Complete_InvalidCrop_Returns400AndLeavesClipDraft(
        double? x, double? y, double? w, double? h)
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { cropX = x, cropY = y, cropWidth = w, cropHeight = h });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_crop");

        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().SingleAsync(c => c.Id == clipId)).Status.Should().Be("draft");
    }

    [Fact]
    public async Task Complete_CropWithCropDisabled_Returns400CropUnavailable()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        await using var factory = new AuthApiFactory(_fx.ConnectionString, _storage, configureServices: services =>
            services.Configure<GankedTV.Api.Services.Media.MediaJobOptions>(o => o.CropEnabled = false));
        using var client = AuthTestHelpers.CreateBearerClient(factory, token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { cropX = 0.1, cropY = 0.0, cropWidth = 0.8, cropHeight = 1.0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("crop_unavailable");
    }

    [Fact]
    public async Task Complete_CropWithTranscodeDisabled_Returns400CropUnavailable()
    {
        // No compress stage means no re-encode to attach the crop filter to.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        await using var factory = new AuthApiFactory(_fx.ConnectionString, _storage, configureServices: services =>
            services.Configure<GankedTV.Api.Services.Media.MediaJobOptions>(o => o.TranscodeEnabled = false));
        using var client = AuthTestHelpers.CreateBearerClient(factory, token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/complete",
            new { cropX = 0.1, cropY = 0.0, cropWidth = 0.8, cropHeight = 1.0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("crop_unavailable");
    }

    [Fact]
    public async Task Complete_Duplicate_SecondCallReturns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);

        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(1024, "video/mp4"));

        using var client = ClientWithBearer(token);
        var first = await client.PostAsync($"/clips/{clipId}/complete", null);
        var second = await client.PostAsync($"/clips/{clipId}/complete", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadAsStringAsync()).Should().Contain("invalid_state");

        // HEAD is skipped on the second call because the state check short-circuits.
        await _storage.Received(1).GetObjectMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
