using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class ClipsReadEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ClipsReadEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private async Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "reader")
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
        DateTimeOffset createdAt,
        string status = "ready",
        string visibility = "public",
        string? title = null)
    {
        var id = Guid.NewGuid();
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = title ?? $"clip-{id:N}".Substring(0, 20),
            VideoKey = $"clips/{userId}/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            Status = status,
            Visibility = visibility,
            DurationSecs = 30,
            Width = 1920,
            Height = 1080,
            FileSizeBytes = 1_000_000,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- GET /clips/feed ----

    [Fact]
    public async Task Feed_Empty_Returns200WithNoItems()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Feed_OrdersByCreatedAtDesc_AndOmitsNonPublicOrNonReady()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var a = await SeedClipAsync(userId, now.AddMinutes(-3), title: "oldest-ready");
        var b = await SeedClipAsync(userId, now.AddMinutes(-2), title: "middle-ready");
        var c = await SeedClipAsync(userId, now.AddMinutes(-1), title: "newest-ready");
        await SeedClipAsync(userId, now, status: "processing", title: "not-ready");
        await SeedClipAsync(userId, now, visibility: "unlisted", title: "unlisted");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(c, b, a);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Feed_PaginationBoundary_ExposesNextCursorAndDrainsOnSecondPage()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var seeded = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            seeded.Add(await SeedClipAsync(userId, now.AddSeconds(-i), title: $"clip-{i}"));
        }

        using var client = _factory!.CreateClient();
        var first = await client.GetAsync("/clips/feed?limit=2");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        firstBody.GetProperty("items").GetArrayLength().Should().Be(2);
        var nextCursor = firstBody.GetProperty("nextCursor").GetString();
        nextCursor.Should().NotBeNullOrEmpty();

        var second = await client.GetAsync($"/clips/feed?limit=2&cursor={Uri.EscapeDataString(nextCursor!)}");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("items").GetArrayLength().Should().Be(1);
        secondBody.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        var returned = firstBody.GetProperty("items").EnumerateArray()
            .Concat(secondBody.GetProperty("items").EnumerateArray())
            .Select(e => e.GetProperty("id").GetGuid())
            .ToList();
        returned.Should().BeEquivalentTo(seeded);
    }

    [Fact]
    public async Task Feed_ExactlyAtLimit_ReturnsNullCursor()
    {
        // Boundary between "full page" and "has more". With limit=2 and exactly 2 ready clips,
        // the limit+1 fetch returns 2 rows, hasMore is false, and nextCursor must be null.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, now.AddSeconds(-1), title: "clip-1");
        await SeedClipAsync(userId, now.AddSeconds(-2), title: "clip-2");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?limit=2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(2);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Feed_LimitClampedToBounds()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            await SeedClipAsync(userId, now.AddSeconds(-i), title: $"clip-{i}");
        }

        using var client = _factory!.CreateClient();

        // limit=0 clamps up to 1
        var low = await client.GetAsync("/clips/feed?limit=0");
        var lowBody = await low.Content.ReadFromJsonAsync<JsonElement>();
        lowBody.GetProperty("items").GetArrayLength().Should().Be(1);

        // limit=999 clamps down to MaxLimit (100) but we only have 3 rows
        var high = await client.GetAsync("/clips/feed?limit=999");
        var highBody = await high.Content.ReadFromJsonAsync<JsonElement>();
        highBody.GetProperty("items").GetArrayLength().Should().Be(3);
        highBody.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Feed_Cursor_IsUrlSafeRawWithoutEscaping()
    {
        // The cursor must survive being dropped into a query string without Uri.EscapeDataString.
        // DateTimeOffset.ToString("O") contains `+` and `:` which URL decoders mishandle; the cursor
        // is Base64Url-encoded to stay opaque and transport-safe.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            await SeedClipAsync(userId, now.AddSeconds(-i), title: $"clip-{i}");
        }

        using var client = _factory!.CreateClient();
        var first = await client.GetAsync("/clips/feed?limit=2");
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var cursor = firstBody.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();
        cursor!.Should().NotContainAny("+", "/", "=", ":");

        // Deliberately pass the cursor raw — no Uri.EscapeDataString. If the token contained `+`
        // the server would see it as a space and drop the filter, returning all 3 items instead of 1.
        var second = await client.GetAsync($"/clips/feed?limit=2&cursor={cursor}");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Feed_IdenticalCreatedAt_PaginatesDeterministicallyWithoutSkips()
    {
        // Composite (CreatedAt, Id) cursor: two clips sharing a microsecond timestamp must not
        // cause one of them to be skipped across page boundaries. The previous CreatedAt-only
        // cursor would drop the second row at each collision point.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var shared = DateTimeOffset.UtcNow;
        var seeded = new[]
        {
            await SeedClipAsync(userId, shared, title: "tie-1"),
            await SeedClipAsync(userId, shared, title: "tie-2"),
            await SeedClipAsync(userId, shared, title: "tie-3"),
        };

        using var client = _factory!.CreateClient();
        var first = await client.GetAsync("/clips/feed?limit=2");
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var nextCursor = firstBody.GetProperty("nextCursor").GetString();
        nextCursor.Should().NotBeNullOrEmpty();

        var second = await client.GetAsync($"/clips/feed?limit=2&cursor={Uri.EscapeDataString(nextCursor!)}");
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("items").GetArrayLength().Should().Be(1);
        secondBody.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        var returned = firstBody.GetProperty("items").EnumerateArray()
            .Concat(secondBody.GetProperty("items").EnumerateArray())
            .Select(e => e.GetProperty("id").GetGuid())
            .ToList();
        returned.Should().BeEquivalentTo(seeded);
    }

    [Fact]
    public async Task Feed_InvalidCursor_SilentlyFallsBackToFirstPage()
    {
        // Contract: a corrupted cursor query string shouldn't break pagination — the client
        // should still get a first page rather than a 400. Guards against accidentally strict
        // parsing if someone later swaps TryParse for Parse.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        await SeedClipAsync(userId, DateTimeOffset.UtcNow);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?cursor=not-a-date");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Feed_NegativeLimit_ClampsToOne()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, now.AddSeconds(-1));
        await SeedClipAsync(userId, now.AddSeconds(-2));

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?limit=-5");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Feed_ExcludesFailedStatusClips()
    {
        // Only "ready" should appear. If the filter ever drifted to `status != "draft"` or
        // similar, "failed" clips would leak.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var ready = await SeedClipAsync(userId, DateTimeOffset.UtcNow.AddSeconds(-1), title: "ready");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, status: "failed", title: "failed");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(ready);
    }

    [Fact]
    public async Task Feed_Anonymous_LikedByMeFalseForAllItems()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, DateTimeOffset.UtcNow);
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = userId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var liked = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("likedByMe").GetBoolean());
        liked.Should().OnlyContain(l => l == false);
    }

    [Fact]
    public async Task Feed_WithJwt_LikedByMeIgnoresOtherUsersLikes()
    {
        // Cross-user isolation: viewer should see likedByMe=false on a clip liked by someone else.
        await _fx.ResetAsync();
        var (_, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (strangerId, _) = await SeedUserAndIssueTokenAsync("stranger");
        var clipId = await SeedClipAsync(authorId, DateTimeOffset.UtcNow);

        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = strangerId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(viewerToken);
        var resp = await client.GetAsync("/clips/feed");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var liked = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("likedByMe").GetBoolean());
        liked.Should().OnlyContain(l => l == false);
    }

    [Fact]
    public async Task Feed_WithJwt_LikedByMeReflectsLikeRows()
    {
        await _fx.ResetAsync();
        var (viewerId, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var now = DateTimeOffset.UtcNow;
        var liked = await SeedClipAsync(authorId, now.AddSeconds(-1), title: "liked");
        var notLiked = await SeedClipAsync(authorId, now.AddSeconds(-2), title: "not-liked");

        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = viewerId, ClipId = liked, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(viewerToken);
        var resp = await client.GetAsync("/clips/feed");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var states = body.GetProperty("items").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetGuid(), e => e.GetProperty("likedByMe").GetBoolean());
        states[liked].Should().BeTrue();
        states[notLiked].Should().BeFalse();
    }

    [Fact]
    public async Task Feed_FeedItemShape_ContainsAuthorAndThumbnailKey()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("shapely");
        var clipId = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "a clip");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items")[0];

        item.GetProperty("id").GetGuid().Should().Be(clipId);
        item.GetProperty("title").GetString().Should().Be("a clip");
        item.GetProperty("thumbnailKey").GetString().Should().Be($"thumbs/{clipId}.jpg");
        item.TryGetProperty("videoUrl", out _).Should().BeFalse("feed items intentionally omit presigned URLs");
        var author = item.GetProperty("author");
        author.GetProperty("id").GetGuid().Should().Be(userId);
        author.GetProperty("username").GetString().Should().Be("shapely");
    }

    // ---- GET /clips/{id} ----

    [Fact]
    public async Task Detail_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync($"/clips/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_NotReady_Returns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, DateTimeOffset.UtcNow, status: "processing");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_Unlisted_Returns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "unlisted");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_Found_ReturnsPresignedUrlAndMetadata()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("owner");
        var clipId = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "playback");

        const string presigned = "https://minio.local/clips/presigned?sig=abc";
        _storage
            .GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(presigned);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(clipId);
        body.GetProperty("title").GetString().Should().Be("playback");
        body.GetProperty("videoUrl").GetString().Should().Be(presigned);
        body.GetProperty("likedByMe").GetBoolean().Should().BeFalse();

        var expiresAt = body.GetProperty("videoUrlExpiresAt").GetDateTimeOffset();
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(2));

        // Expiry passed to the storage service should be roughly one hour.
        _storage.Received(1).GetPresignedGetUrl(
            Arg.Any<string>(),
            $"clips/{clipId}.mp4",
            Arg.Is<TimeSpan?>(ts => ts.HasValue && ts.Value == TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task Detail_WithJwt_LikedByMeFalseWhenNoLike()
    {
        // Exercises the authed code path specifically — anonymous false is covered above, but
        // JWT-present-without-like is a distinct branch (TryGetUserId returns true, AnyAsync false).
        await _fx.ResetAsync();
        var (_, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(authorId, DateTimeOffset.UtcNow);

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://example/url");

        using var client = ClientWithBearer(viewerToken);
        var resp = await client.GetAsync($"/clips/{clipId}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likedByMe").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Feed_Authed_EmptyFeed_ShortCircuitsLikeLookup()
    {
        // Covers the `ids.Count == 0` early-return in LoadLikedClipIdsAsync: authed viewer
        // calls the feed with no visible clips, so the likedIds lookup must skip the DB
        // round-trip rather than issuing a WHERE IN () query.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("empty-viewer");

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/clips/feed");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Feed_Base64ValidCursorWithMalformedPayload_FallsBackToFirstPage()
    {
        // Base64url-valid payload but no "_" separator — exercises TryParseCursor's structural
        // guard (sep <= 0) distinct from the base64 decode catch already covered.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        await SeedClipAsync(userId, DateTimeOffset.UtcNow);

        var malformed = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("no-separator"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/feed?cursor={malformed}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Feed_CursorWithTrailingSeparator_FallsBackToFirstPage()
    {
        // sep == decoded.Length - 1: payload ends in `_` with an empty GUID part. Distinct
        // branch from sep <= 0.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        await SeedClipAsync(userId, DateTimeOffset.UtcNow);

        var trailing = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("2026-04-20T00:00:00.0000000+00:00_"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/feed?cursor={trailing}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Detail_WithJwt_LikedByMeTrueWhenLikeExists()
    {
        await _fx.ResetAsync();
        var (viewerId, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(authorId, DateTimeOffset.UtcNow);

        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = viewerId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://example/url");

        using var client = ClientWithBearer(viewerToken);
        var resp = await client.GetAsync($"/clips/{clipId}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likedByMe").GetBoolean().Should().BeTrue();
    }
}
