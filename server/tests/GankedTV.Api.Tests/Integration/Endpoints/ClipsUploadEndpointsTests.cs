using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
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

    [Fact]
    public async Task Create_InvalidVisibility_Returns400()
    {
        // Visibility validation lives in ClipUploadService (case-insensitive allowed-values)
        // rather than a DataAnnotation, so this surfaces via ProblemResults with code=invalid_visibility.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new { title = "x", visibility = "private" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_visibility");
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
