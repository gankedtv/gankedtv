using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
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

    private async Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "owner")
    {
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = username,
                Email = $"{username}@example.com",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        using var scope = _factory!.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var token = jwt.Issue(new User { Id = id, Username = username, Email = $"{username}@example.com" });
        return (id, token);
    }

    private HttpClient ClientWithBearer(string token)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> SeedClipAsync(
        Guid userId,
        string title = "seed",
        string? description = null,
        string visibility = "public",
        string status = "ready",
        int? gameId = null,
        string? thumbnailKey = null,
        DateTimeOffset? createdAt = null)
    {
        var id = Guid.NewGuid();
        var seeded = createdAt ?? DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = title,
            Description = description,
            GameId = gameId,
            VideoKey = $"clips/{id}.mp4",
            ThumbnailKey = thumbnailKey,
            Status = status,
            Visibility = visibility,
            CreatedAt = seeded,
            UpdatedAt = seeded,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- PATCH /clips/{id} ----

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
    public async Task Patch_UpdatesVisibility_Persists()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, visibility: "public");

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { visibility = "unlisted" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        var persisted = await db.Clips.AsNoTracking().FirstAsync(c => c.Id == clipId);
        persisted.Visibility.Should().Be("unlisted");
    }

    [Fact]
    public async Task Patch_InvalidVisibility_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { visibility = "private" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
            $"clips/{clipId}.mp4",
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
        var minio = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<MinioOptions>>()
            .Value;

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var db = _fx.CreateContext();
        (await db.Clips.AnyAsync(c => c.Id == clipId)).Should().BeFalse();

        // Video goes to ClipsBucket, thumbnail goes to ThumbnailsBucket — a swap would leak the
        // video key into the thumbnails bucket (and vice versa), silently breaking cleanup.
        await _storage.Received(1).DeleteObjectAsync(
            minio.ClipsBucket,
            $"clips/{clipId}.mp4",
            Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteObjectAsync(
            minio.ThumbnailsBucket,
            "thumbs/x.jpg",
            Arg.Any<CancellationToken>());
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
}
