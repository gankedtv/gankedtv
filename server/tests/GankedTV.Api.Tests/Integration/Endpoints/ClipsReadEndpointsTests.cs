using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "reader") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<(Guid id, string shareCode)> SeedClipAsync(
        Guid userId,
        DateTimeOffset createdAt,
        string status = "ready",
        string visibility = "public",
        string? title = null,
        int? gameId = null)
    {
        var id = Guid.NewGuid();
        var shareCode = ShareCodeGenerator.Next();
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            GameId = gameId,
            Title = title ?? $"clip-{id:N}".Substring(0, 20),
            VideoKey = $"{userId}/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = shareCode,
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
        return (id, shareCode);
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
        var (a, _) = await SeedClipAsync(userId, now.AddMinutes(-3), title: "oldest-ready");
        var (b, _) = await SeedClipAsync(userId, now.AddMinutes(-2), title: "middle-ready");
        var (c, _) = await SeedClipAsync(userId, now.AddMinutes(-1), title: "newest-ready");
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
            var (clipId_i, _) = await SeedClipAsync(userId, now.AddSeconds(-i), title: $"clip-{i}");
            seeded.Add(clipId_i);
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
        var (tie1, _) = await SeedClipAsync(userId, shared, title: "tie-1");
        var (tie2, _) = await SeedClipAsync(userId, shared, title: "tie-2");
        var (tie3, _) = await SeedClipAsync(userId, shared, title: "tie-3");
        var seeded = new[] { tie1, tie2, tie3 };

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
        var (ready, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow.AddSeconds(-1), title: "ready");
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
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);
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
        var (clipId, _) = await SeedClipAsync(authorId, DateTimeOffset.UtcNow);

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
        var (liked, _) = await SeedClipAsync(authorId, now.AddSeconds(-1), title: "liked");
        var (notLiked, _) = await SeedClipAsync(authorId, now.AddSeconds(-2), title: "not-liked");

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
    public async Task Feed_FeedItemShape_ContainsAuthorAndThumbnailUrl()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("shapely");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "a clip");

        const string presignedThumb = "https://minio.local/thumbs/presigned?sig=t";
        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns(presignedThumb);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items")[0];

        item.GetProperty("id").GetGuid().Should().Be(clipId);
        item.GetProperty("title").GetString().Should().Be("a clip");
        item.GetProperty("shareCode").GetString().Should().NotBeNullOrEmpty();
        // The feed exposes a presigned thumbnail URL, never the raw bucket key.
        item.GetProperty("thumbnailUrl").GetString().Should().Be(presignedThumb);
        item.TryGetProperty("thumbnailKey", out _).Should().BeFalse(
            "raw bucket keys are not part of the public contract");
        item.TryGetProperty("videoUrl", out _).Should().BeFalse(
            "feed items intentionally omit video presigned URLs");
        var author = item.GetProperty("author");
        author.GetProperty("id").GetGuid().Should().Be(userId);
        author.GetProperty("username").GetString().Should().Be("shapely");
    }


    [Fact]
    public async Task Feed_GameProjection_PopulatedWhenSet_NullWhenNotSet()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (withGame, _) = await SeedClipAsync(userId, now.AddSeconds(-1), title: "with-game", gameId: 2);
        var (withoutGame, _) = await SeedClipAsync(userId, now.AddSeconds(-2), title: "no-game");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var byId = body.GetProperty("items").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetGuid(), e => e);

        var gameNode = byId[withGame].GetProperty("game");
        gameNode.ValueKind.Should().Be(JsonValueKind.Object);
        gameNode.GetProperty("id").GetInt32().Should().Be(2);
        gameNode.GetProperty("slug").GetString().Should().Be("valorant");
        gameNode.GetProperty("tag").GetString().Should().Be("VALORANT");

        byId[withoutGame].GetProperty("game").ValueKind.Should().Be(JsonValueKind.Null);
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
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, status: "processing");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_Unlisted_ReturnsOkForAnyone()
    {
        // Unlisted = accessible via direct link; visibility is only enforced at the feed layer.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "unlisted");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Detail_Found_ReturnsPresignedUrlAndMetadata()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("owner");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "playback");

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
        body.GetProperty("shareCode").GetString().Should().NotBeNullOrEmpty();

        var expiresAt = body.GetProperty("videoUrlExpiresAt").GetDateTimeOffset();
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(2));

        // Expiry passed to the storage service should be roughly one hour.
        _storage.Received(1).GetPresignedGetUrl(
            Arg.Any<string>(),
            $"{userId}/{clipId}.mp4",
            Arg.Is<TimeSpan?>(ts => ts.HasValue && ts.Value == TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task Detail_GameProjection_PopulatedWhenSet_NullWhenNotSet()
    {
        // Mirrors the feed-side projection test for the detail endpoint so a future
        // regression in ToDetail() / `.Include(c => c.Game)` on detail is caught.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (withGame, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "with-game", gameId: 2);
        var (withoutGame, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "no-game");

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://example/url");

        using var client = _factory!.CreateClient();

        var withResp = await client.GetAsync($"/clips/{withGame}");
        withResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var withBody = await withResp.Content.ReadFromJsonAsync<JsonElement>();
        var game = withBody.GetProperty("game");
        game.ValueKind.Should().Be(JsonValueKind.Object);
        game.GetProperty("id").GetInt32().Should().Be(2);
        game.GetProperty("slug").GetString().Should().Be("valorant");
        game.GetProperty("tag").GetString().Should().Be("VALORANT");

        var withoutResp = await client.GetAsync($"/clips/{withoutGame}");
        var withoutBody = await withoutResp.Content.ReadFromJsonAsync<JsonElement>();
        withoutBody.GetProperty("game").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Detail_WithJwt_LikedByMeFalseWhenNoLike()
    {
        // Exercises the authed code path specifically — anonymous false is covered above, but
        // JWT-present-without-like is a distinct branch (TryGetUserId returns true, AnyAsync false).
        await _fx.ResetAsync();
        var (_, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (clipId, _) = await SeedClipAsync(authorId, DateTimeOffset.UtcNow);

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
        var (clipId, _) = await SeedClipAsync(authorId, DateTimeOffset.UtcNow);

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

    // ---- GET /c/{code} ----

    [Fact]
    public async Task ShareCodeResolve_Found_ReturnsSameShapeAsDetail()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (id, shareCode) = await SeedClipAsync(userId, now);

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient();
        var byCode = await client.GetAsync($"/c/{shareCode}");
        var byId   = await client.GetAsync($"/clips/{id}");

        byCode.StatusCode.Should().Be(HttpStatusCode.OK);
        byId.StatusCode.Should().Be(HttpStatusCode.OK);

        var codeJson = await byCode.Content.ReadFromJsonAsync<JsonElement>();
        var idJson   = await byId.Content.ReadFromJsonAsync<JsonElement>();

        // Both routes return the same DTO — compare fields that don't change between calls
        codeJson.GetProperty("id").GetGuid().Should().Be(idJson.GetProperty("id").GetGuid());
        codeJson.GetProperty("shareCode").GetString().Should().Be(shareCode);
        codeJson.GetProperty("title").GetString().Should().Be(idJson.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ShareCodeResolve_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/c/notexist");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ShareCodeResolve_NotReady_Returns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, status: "processing");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ShareCodeResolve_Unlisted_ReturnsOkForAnyone()
    {
        // Share code = direct link; visibility is only enforced at the feed layer.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "unlisted");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
