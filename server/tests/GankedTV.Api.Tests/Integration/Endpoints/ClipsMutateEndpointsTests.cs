using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Caching;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresClips")]
public class ClipsMutateEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ClipsMutateEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _storage = Substitute.For<IObjectStorageService>();
        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://minio.local/presigned");
        _factory = new AuthApiFactory(_fx.ConnectionString, _storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "owner") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    // Sentinel so the helper can tell "thumbnailKey not specified — synthesize a
    // sensible default" apart from "explicitly passed null" (used by the cleanup
    // test that exercises the no-thumbnail defensive branch).
    private const string DefaultThumbnailKey = "<<default>>";

    private async Task<Guid> SeedClipAsync(
        Guid userId,
        string title = "seed",
        string? description = null,
        string visibility = "public",
        string status = "ready",
        int? gameId = null,
        string? thumbnailKey = DefaultThumbnailKey,
        string uploadSource = "web",
        DateTimeOffset? createdAt = null,
        short? durationSecs = null)
    {
        var id = Guid.NewGuid();
        var seeded = createdAt ?? DateTimeOffset.UtcNow;
        // Ready clips always have a thumbnail key (the worker is the only path to
        // Ready and never marks Ready without one). Synthesize a placeholder when the
        // caller didn't specify so the strict ToDetail mapping doesn't blow up.
        var resolvedThumbKey = thumbnailKey == DefaultThumbnailKey
            ? (status == ClipStatuses.Ready ? $"thumbs/{id}.jpg" : null)
            : thumbnailKey;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = title,
            Description = description,
            GameId = gameId,
            VideoKey = $"clips/{userId}/{id}.mp4",
            ThumbnailKey = resolvedThumbKey,
            ShareCode = ShareCodeGenerator.Next(),
            Status = status,
            Visibility = visibility,
            UploadSource = uploadSource,
            DurationSecs = durationSecs,
            CreatedAt = seeded,
            UpdatedAt = seeded,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- PATCH /clips/{id} ----

    [Fact]
    public async Task Patch_FeedCacheInvalidationThrows_StillReturns200()
    {
        // Codifies the spec's "Redis unavailable → no 500s": the DB write has committed, so a
        // throwing best-effort invalidation must not surface as a 500. Overrides IFeedCache with
        // a substitute that throws on InvalidateFeedsAsync.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, title: "before");

        var throwingCache = Substitute.For<IFeedCache>();
        throwingCache.When(c => c.InvalidateFeedsAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("redis down"));
        using var factory = _factory!.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IFeedCache>();
            s.AddSingleton(throwingCache);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "after" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().Where(c => c.Id == clipId).Select(c => c.Title).FirstAsync())
            .Should().Be("after"); // the write persisted despite the cache failure
    }

    [Fact]
    public async Task Patch_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PatchAsJsonAsync($"/clips/{Guid.NewGuid()}", new { title = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Patch_ClipMissing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync($"/clips/{Guid.NewGuid()}", new { title = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_NonOwner_Returns403()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("owner");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("other");
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(otherToken);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "hijacked" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Patch_NonOwner_InvalidField_Returns403NotBadRequest()
    {
        // Locks in ownership-before-validation ordering. If the checks were reordered, a
        // non-owner sending an invalid body would get 400 instead of 403 — which would tell
        // them the clip exists and their payload was parsed. Non-owners shouldn't learn either.
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("owner");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("other");
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(otherToken);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { visibility = "bogus" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("processing")]
    [InlineData("failed")]
    public async Task Patch_NonReadyClip_Returns409InvalidState(string status)
    {
        // PATCH only operates on Ready clips — matches GET /clips/{id}'s Ready filter so
        // the response shape (ClipDetailResponse with non-null ThumbnailUrl) stays valid.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, status: status);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "edited" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Pin the problem code so a future regression that returns 409 for a different
        // reason (e.g. the username-conflict path leaking through) doesn't pass silently.
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_state");
    }

    [Fact]
    public async Task Patch_NullBody_Returns400()
    {
        // Literal JSON `null` deserializes to a null UpdateClipRequest reference; the endpoint
        // must reject it explicitly rather than NRE on the first property read.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync<object?>($"/clips/{clipId}", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_EmptyObject_ReturnsOk_BumpsUpdatedAt()
    {
        // `{}` is a valid PATCH with no field updates — the contract is that it still bumps
        // UpdatedAt and returns 200. If that ever flips to 400 or a no-op response, clients
        // depending on "touch to refresh" would silently break.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var seededAt = DateTimeOffset.UtcNow.AddHours(-1);
        var clipId = await SeedClipAsync(ownerId, createdAt: seededAt);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.UpdatedAt.Should().BeAfter(seededAt);
    }

    [Fact]
    public async Task Patch_Owner_UpdatesTitle_ReturnsUpdatedDetail()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, title: "original", description: "before");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "renamed" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(clipId);
        body.GetProperty("title").GetString().Should().Be("renamed");
        body.GetProperty("description").GetString().Should().Be("before");

        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.Title.Should().Be("renamed");
        persisted.Description.Should().Be("before");
    }

    [Fact]
    public async Task Patch_NeverChangesUploadSource()
    {
        // Upload provenance is stamped once at create and drives the verified badge; it must
        // survive every edit. Guards against UpdateClipRequest ever growing an uploadSource
        // field — today it's immutable by construction, but nothing else would catch that.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, title: "keyed", uploadSource: "api");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync(
            $"/clips/{clipId}",
            new { title = "renamed", visibility = "unlisted", uploadSource = "web" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("uploadSource").GetString().Should().Be("api");

        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.UploadSource.Should().Be("api");
        persisted.Title.Should().Be("renamed");
    }

    [Theory]
    [InlineData("unlisted")]
    [InlineData("private")]
    public async Task Patch_UpdatesVisibility_Persists(string visibility)
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, visibility: "public");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { visibility });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.Visibility.Should().Be(visibility);
    }

    [Theory]
    [InlineData("hidden")] // moderation-owned, never user-settable
    [InlineData("secret")] // unknown value
    public async Task Patch_InvalidVisibility_Returns400(string visibility)
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { visibility });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_HiddenClip_Returns403Moderated_AndDoesNotResurrect()
    {
        // A hidden clip is a moderation takedown. The owner PATCHing visibility=public must not
        // undo it — the endpoint refuses the whole mutation and the row stays hidden.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, visibility: "hidden");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { visibility = "public" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("moderated");

        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.Visibility.Should().Be("hidden");
    }

    [Fact]
    public async Task Patch_HiddenClip_RefusesMetadataEdits()
    {
        // The block covers every field, not just visibility — the owner can't edit title/desc
        // of a taken-down clip either.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, title: "before", visibility: "hidden");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "renamed" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId)).Title.Should().Be("before");
    }

    [Fact]
    public async Task Patch_TitleTooLong_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = new string('a', 256) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_EmptyTitle_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "   " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_DescriptionTooLong_Returns400()
    {
        // MaxDescriptionLength defaults to 5000 in ClipValidationOptions; 5001 trips the cap
        // and keeps PATCH aligned with the upload-side validation (ClipUploadService).
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { description = new string('a', 5001) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_NonExistentGameId_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { gameId = 999_999 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_ValidGameId_Persists()
    {
        // Game id 1 ("League of Legends") is seeded by the initial migration and survives
        // PostgresFixture resets (games are in TablesToIgnore).
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { gameId = 1 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.GameId.Should().Be(1);
    }

    [Fact]
    public async Task Patch_UpdatesDescription_Persists()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, description: "before");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { description = "after" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("description").GetString().Should().Be("after");

        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.Description.Should().Be("after");
    }

    [Fact]
    public async Task Patch_MultiField_AppliesAll()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(
            ownerId,
            title: "original",
            description: "orig desc",
            visibility: "public",
            gameId: null);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new
        {
            title = "new title",
            description = "new desc",
            visibility = "unlisted",
            gameId = 2,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.Title.Should().Be("new title");
        persisted.Description.Should().Be("new desc");
        persisted.Visibility.Should().Be("unlisted");
        persisted.GameId.Should().Be(2);
    }

    [Fact]
    public async Task Patch_BumpsUpdatedAt()
    {
        // Seed the clip an hour in the past so the UpdatedAt bump is unambiguously observable;
        // same-tick resolution with DateTimeOffset.UtcNow would make the assertion flaky.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var seededAt = DateTimeOffset.UtcNow.AddHours(-1);
        var clipId = await SeedClipAsync(ownerId, createdAt: seededAt);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "bump" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.UpdatedAt.Should().BeAfter(seededAt);
        persisted.CreatedAt.Should().BeCloseTo(seededAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Patch_Response_IncludesFreshPresignedUrl()
    {
        // PATCH response reuses ToDetail → videoUrl must be a freshly-presigned URL for the clip's
        // current VideoKey, with expiry roughly one hour out.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        const string presigned = "https://minio.local/fresh?sig=abc";
        _storage
            .GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(presigned);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "fresh" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("videoUrl").GetString().Should().Be(presigned);

        var expiresAt = body.GetProperty("videoUrlExpiresAt").GetDateTimeOffset();
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(2));

        _storage.Received().GetPresignedGetUrl(
            Arg.Any<string>(),
            $"clips/{ownerId}/{clipId}.mp4",
            Arg.Is<TimeSpan?>(ts => ts.HasValue && ts.Value == TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task Patch_Response_LikedByMe_ReflectsViewerLikeRow()
    {
        // The owner PATCHing their own clip may or may not have a like row themselves. Seed one
        // so `likedByMe` must come out true, proving the lookup uses the authed userId rather
        // than a default/false shortcut.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);
        await using (var seed = _fx.CreateContext())
        {
            seed.Likes.Add(new Like { UserId = ownerId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "liked-by-owner" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likedByMe").GetBoolean().Should().BeTrue();
    }

    // ---- DELETE /clips/{id} ----

    [Fact]
    public async Task Delete_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.DeleteAsync($"/clips/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_ClipMissing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.DeleteAsync($"/clips/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_NonOwner_Returns403()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("owner");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("other");
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(otherToken);
        var resp = await client.DeleteAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_Owner_Returns204_RemovesRow_DeletesS3Objects()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, thumbnailKey: "thumbs/x.jpg");

        using var scope = _factory!.Services.CreateScope();
        var s3 = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<S3Options>>()
            .Value;

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var db = _fx.CreateContext();
        (await db.Clips.AnyAsync(c => c.Id == clipId)).Should().BeFalse();

        // Video goes to ClipsBucket, thumbnail goes to ThumbnailsBucket — a swap would leak the
        // video key into the thumbnails bucket (and vice versa), silently breaking cleanup.
        // Non-cancellable token: the row is already gone, so a client disconnect must not
        // abort the cleanup (same contract as the hide purge).
        await _storage.Received(1).DeleteObjectAsync(
            s3.ClipsBucket,
            $"clips/{ownerId}/{clipId}.mp4",
            Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
        await _storage.Received(1).DeleteObjectAsync(
            s3.ThumbnailsBucket,
            "thumbs/x.jpg",
            Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task Delete_Owner_NoThumbnail_SkipsThumbnailBucketDelete()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, thumbnailKey: null);

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Exactly one storage delete call — for the video — since there was no thumbnail.
        await _storage.Received(1).DeleteObjectAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_CascadesLikes()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync("owner");
        var (likerId, _) = await SeedUserAndIssueTokenAsync("liker");
        var clipId = await SeedClipAsync(ownerId);
        await using (var seed = _fx.CreateContext())
        {
            seed.Likes.Add(new Like { UserId = likerId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = _fx.CreateContext();
        (await db.Likes.AnyAsync(l => l.ClipId == clipId)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_S3FailureStillReturns204()
    {
        // Best-effort S3 cleanup: DB row is already gone, so a storage failure must not surface
        // as 500. Covers the catch block in DeleteClip.
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        _storage
            .DeleteObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("minio down")));

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = _fx.CreateContext();
        (await db.Clips.AnyAsync(c => c.Id == clipId)).Should().BeFalse();
    }

    // ---- POST /clips/{id}/trim ----

    private static object TrimBody(double start, double end) =>
        new { trimStartSeconds = start, trimEndSeconds = end };

    [Fact]
    public async Task Trim_Owner_RequeuesClipWithNewRange()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(2.5, 12.75));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain(ClipStatuses.Processing);

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        // Back at the head of the pipeline so the poster is re-cut alongside the master.
        clip.Status.Should().Be(ClipStatuses.Processing);
        clip.TrimStartSecs.Should().Be(2.5);
        clip.TrimEndSecs.Should().Be(12.75);
        clip.EditedAt.Should().NotBeNull();
        clip.EditCount.Should().Be(1);
        clip.ProcessingAttempts.Should().Be(0);
        clip.ProcessingStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Trim_PurgesCachedHlsLadder()
    {
        // The JIT ladder is keyed by clip id alone, so a stale one would keep serving the
        // pre-cut footage to devices that can't decode the master.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await _storage.Received(1).DeleteByPrefixAsync(
            Arg.Any<string>(), $"{clipId:N}/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trim_SecondCallWhileProcessing_Returns409()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        using var client = ClientWithBearer(token);
        (await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1, 5)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(2, 6));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain("invalid_state");

        await using var db = _fx.CreateContext();
        // The losing call must not bump the generation counter — it would desync the
        // compressed-master key from the row the worker is about to claim.
        (await db.Clips.AsNoTracking().Where(c => c.Id == clipId).Select(c => c.EditCount).FirstAsync())
            .Should().Be(1);
    }

    [Fact]
    public async Task Trim_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync($"/clips/{Guid.NewGuid()}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Trim_ClipMissing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync($"/clips/{Guid.NewGuid()}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Trim_NonOwner_Returns403()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("owner");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("other");
        var clipId = await SeedClipAsync(ownerId, durationSecs: 30);

        using var client = ClientWithBearer(otherToken);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().Where(c => c.Id == clipId).Select(c => c.Status).FirstAsync())
            .Should().Be(ClipStatuses.Ready);
    }

    [Fact]
    public async Task Trim_HiddenClip_Returns403Moderated()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, visibility: ClipVisibilities.Hidden, durationSecs: 30);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("moderated");
    }

    [Theory]
    [InlineData(ClipStatuses.Processing)]
    [InlineData(ClipStatuses.Transcoding)]
    [InlineData(ClipStatuses.Failed)]
    public async Task Trim_NotReady_Returns409(string status)
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, status: status, durationSecs: 30);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_state");
    }

    [Theory]
    [InlineData(-1.0, 5.0)]      // negative start
    [InlineData(1.0, 1.1)]       // span below the 0.2s minimum
    [InlineData(5.0, 1.0)]       // inverted range
    [InlineData(31.0, 40.0)]     // starts past the end of a 30s clip
    public async Task Trim_InvalidRange_Returns400(double start, double end)
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(start, end));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_trim");
    }

    [Fact]
    public async Task Trim_ExactMinimumSpan_IsAccepted()
    {
        // FP representation of 1.7 - 1.5 lands just under 0.2; the epsilon must absorb it.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1.5, 1.7));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Trim_MissingOffset_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", new { trimStartSeconds = 1.0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Trim_WithTranscodeDisabled_Returns400TrimUnavailable()
    {
        // Without the compress stage there is nothing to apply the cut — accepting the request
        // would silently publish the untrimmed clip back to 'ready'.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        await using var factory = new AuthApiFactory(_fx.ConnectionString, _storage, configureServices: services =>
            services.Configure<GankedTV.Api.Services.Media.MediaJobOptions>(o => o.TranscodeEnabled = false));
        using var client = AuthTestHelpers.CreateBearerClient(factory, token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("trim_unavailable");
    }

    [Fact]
    public async Task Trim_FeedCacheInvalidationThrows_StillReturns200()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        var throwingCache = Substitute.For<IFeedCache>();
        throwingCache.When(c => c.InvalidateFeedsAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("redis down"));
        using var factory = _factory!.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IFeedCache>();
            s.AddSingleton(throwingCache);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        (await db.Clips.AsNoTracking().Where(c => c.Id == clipId).Select(c => c.Status).FirstAsync())
            .Should().Be(ClipStatuses.Processing);
    }

    [Fact]
    public async Task Trim_StreamCachePurgeThrows_StillReturns200()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, durationSecs: 30);

        _storage.DeleteByPrefixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("minio down")));

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/trim", TrimBody(1, 5));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
