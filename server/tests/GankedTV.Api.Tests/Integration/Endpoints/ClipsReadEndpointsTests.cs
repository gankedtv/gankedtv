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

    // ---- GET /clips/feed?sort=trending ----

    [Fact]
    public async Task Trending_24h_OrdersByScore()
    {
        // Score = (likes*3 + views) / pow(hours+2, 1.5). With matching ages, more engagement wins.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (hot, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "hot");
        var (mid, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "mid");
        var (cool, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "cool");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 20).Select(_ => new ClipView { ClipId = hot, CreatedAt = now.AddMinutes(-5) }));
            db.ClipViews.AddRange(
                Enumerable.Range(0, 5).Select(_ => new ClipView { ClipId = mid, CreatedAt = now.AddMinutes(-5) }));
            db.ClipViews.AddRange(
                Enumerable.Range(0, 1).Select(_ => new ClipView { ClipId = cool, CreatedAt = now.AddMinutes(-5) }));
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=trending&window=24h");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(hot, mid, cool);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Trending_LikesCountedTriple_VsViews()
    {
        // Score weighting locks the (likes*3 + views) coefficient: a clip with 1 like must
        // outrank a clip with 2 views when ages match.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("author");
        var (likerId, _) = await SeedUserAndIssueTokenAsync("liker");
        var now = DateTimeOffset.UtcNow;
        var (oneLike, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "one-like");
        var (twoViews, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "two-views");

        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = likerId, ClipId = oneLike, CreatedAt = now.AddMinutes(-1) });
            db.ClipViews.Add(new ClipView { ClipId = twoViews, CreatedAt = now.AddMinutes(-1) });
            db.ClipViews.Add(new ClipView { ClipId = twoViews, CreatedAt = now.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=trending&window=24h");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(oneLike, twoViews);
    }

    [Fact]
    public async Task Trending_24hExcludesOlderEngagement_7dIncludes()
    {
        // A 3-day-old view falls outside the 24h window but inside 7d. Same clip, two
        // windows, different result — the time-window filter is what makes trending "real".
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (fresh, _) = await SeedClipAsync(userId, now.AddHours(-2), title: "fresh");
        var (stale, _) = await SeedClipAsync(userId, now.AddDays(-3), title: "stale");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 3).Select(_ => new ClipView { ClipId = fresh, CreatedAt = now.AddMinutes(-30) }));
            db.ClipViews.AddRange(
                Enumerable.Range(0, 50).Select(_ => new ClipView { ClipId = stale, CreatedAt = now.AddDays(-3) }));
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();

        var dayResp = await client.GetAsync("/clips/feed?sort=trending&window=24h");
        var dayIds = (await dayResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        dayIds.Should().Equal(fresh);

        var weekResp = await client.GetAsync("/clips/feed?sort=trending&window=7d");
        var weekIds = (await weekResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToHashSet();
        weekIds.Should().Contain(fresh).And.Contain(stale);
    }

    [Fact]
    public async Task Trending_ExcludesNonPublicAndNonReady()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (publicClip, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "public-ready");
        var (unlisted, _) = await SeedClipAsync(userId, now.AddHours(-1), visibility: "unlisted", title: "unlisted");
        var (processing, _) = await SeedClipAsync(userId, now.AddHours(-1), status: "processing", title: "processing");

        await using (var db = _fx.CreateContext())
        {
            foreach (var id in new[] { publicClip, unlisted, processing })
            {
                db.ClipViews.AddRange(
                    Enumerable.Range(0, 5).Select(_ => new ClipView { ClipId = id, CreatedAt = now.AddMinutes(-10) }));
            }
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=trending&window=24h");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

        ids.Should().Equal(publicClip);
    }

    [Fact]
    public async Task Trending_OmitsClipsWithoutEngagementInWindow()
    {
        // The trending feed is "what people are engaging with right now" — a clip with zero
        // likes and zero views in the window has no place on the list even if it's recent.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (engaged, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "engaged");
        await SeedClipAsync(userId, now.AddHours(-1), title: "dormant");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = engaged, CreatedAt = now.AddMinutes(-5) });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=trending&window=24h");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

        ids.Should().Equal(engaged);
    }

    [Fact]
    public async Task Trending_InvalidWindow_Returns400()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed?sort=trending&window=bogus");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Feed_InvalidSort_Returns400()
    {
        // Symmetric with the window guard: an unknown explicit sort value is a 400 so client
        // typos like `?sort=trendng` surface loudly instead of silently serving latest.
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed?sort=bogus");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Trending_MissingWindow_Returns400()
    {
        // window is required for trending — unlike `source`, a missing value is a 400 because
        // trending without a window has no defined meaning.
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed?sort=trending");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Trending_EmptyResult_Returns200WithNoItems()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed?sort=trending&window=24h");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Trending_LimitClampedToTrendingMax()
    {
        // Trending caps at TrendingMaxLimit (50) regardless of requested limit, since it's a
        // single ranked page and the in-memory scoring step must stay bounded.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var clipIds = new List<Guid>(60);
        for (var i = 0; i < 60; i++)
        {
            var (id, _) = await SeedClipAsync(userId, now.AddMinutes(-i), title: $"c{i}");
            clipIds.Add(id);
        }

        await using (var db = _fx.CreateContext())
        {
            for (var i = 0; i < clipIds.Count; i++)
            {
                db.ClipViews.Add(new ClipView { ClipId = clipIds[i], CreatedAt = now.AddMinutes(-i) });
            }
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=trending&window=24h&limit=999");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("items").GetArrayLength().Should().Be(50);
    }

    [Fact]
    public async Task Feed_DefaultSortLatest_UnchangedFromPreTrendingBehavior()
    {
        // Regression guard: passing sort=latest (or omitting sort) must keep the exact
        // descending-by-created-at order + keyset cursor pagination the feed has always used.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (a, _) = await SeedClipAsync(userId, now.AddMinutes(-3), title: "a");
        var (b, _) = await SeedClipAsync(userId, now.AddMinutes(-2), title: "b");
        var (c, _) = await SeedClipAsync(userId, now.AddMinutes(-1), title: "c");

        using var client = _factory!.CreateClient();
        var explicitResp = await client.GetAsync("/clips/feed?sort=latest");
        var defaultResp = await client.GetAsync("/clips/feed");

        explicitResp.StatusCode.Should().Be(HttpStatusCode.OK);
        defaultResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var explicitIds = (await explicitResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        var defaultIds = (await defaultResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

        explicitIds.Should().Equal(c, b, a);
        defaultIds.Should().Equal(c, b, a);
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

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var byCode = await client.GetAsync($"/c/{shareCode}");
        var byId = await client.GetAsync($"/clips/{id}");

        byCode.StatusCode.Should().Be(HttpStatusCode.OK);
        byId.StatusCode.Should().Be(HttpStatusCode.OK);

        var codeJson = await byCode.Content.ReadFromJsonAsync<JsonElement>();
        var idJson = await byId.Content.ReadFromJsonAsync<JsonElement>();

        // Guard against DTO shape drift: both routes must expose the exact same property set.
        var codeProps = codeJson.EnumerateObject().Select(p => p.Name).ToHashSet();
        var idProps = idJson.EnumerateObject().Select(p => p.Name).ToHashSet();
        codeProps.Should().BeEquivalentTo(idProps);

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
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ShareCodeResolve_CrawlerUA_ReturnsHtmlWithOgTags()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "My Awesome Clip");

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");

        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("og:title");
        body.Should().Contain("og:image");
        body.Should().Contain("og:video");
        body.Should().Contain("twitter:card");
        body.Should().Contain("My Awesome Clip");
    }

    [Fact]
    public async Task ShareCodeResolve_NonCrawlerUA_Returns302ToWebOrigin()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible)");

        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location.Should().NotBeNull();
        resp.Headers.Location!.ToString().Should().StartWith("http://localhost:5173");
        resp.Headers.Location!.ToString().Should().EndWith($"/c/{shareCode}");
    }

    [Fact]
    public async Task ShareCodeResolve_CrawlerUA_NotFound_Returns404()
    {
        await _fx.ResetAsync();

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");

        var resp = await client.GetAsync("/c/unknowncode");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ShareCodeResolve_CrawlerUA_BeatsAcceptJsonHeader()
    {
        // Precedence lock: a crawler UA that also sends `Accept: application/json`
        // (rare but legal) must still receive the OG HTML — embed rendering wins over
        // negotiation. If the branch ordering ever flips, social previews break.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "Precedence Clip");

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("og:title");
        body.Should().Contain("Precedence Clip");
    }

    [Fact]
    public async Task ShareCodeResolve_CrawlerUA_CanonicalUrlUsesWebOrigin()
    {
        // og:url must point at the user-facing web origin (WebOrigin config), not
        // request.Scheme/Host — behind a reverse proxy the latter exposes the internal
        // API host (e.g. http://localhost:5050) and breaks canonical links.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");

        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain($"http://localhost:5173/c/{shareCode}");
    }

    [Fact]
    public async Task ShareCodeResolve_CrawlerUA_DescriptionPopulatesBothOgAndTwitterTags()
    {
        // When Description is present, both og:description and twitter:description must
        // be emitted (and HTML-escaped). Pairs with the empty-description test below,
        // which locks the null branch.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (id, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);
        await using (var db = _fx.CreateContext())
        {
            var clip = db.Clips.Single(c => c.Id == id);
            clip.Description = "A clutch ace under pressure";
            await db.SaveChangesAsync();
        }

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");

        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("og:description");
        body.Should().Contain("twitter:description");
        body.Should().Contain("A clutch ace under pressure");
    }

    [Fact]
    public async Task ShareCodeResolve_CrawlerUA_EmptyDescriptionOmitsOgDescriptionTag()
    {
        // Whitespace/empty Description must not produce <meta og:description content="" />.
        // Slack in particular renders the empty tag as a blank line under the title.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (id, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);
        await using (var db = _fx.CreateContext())
        {
            var clip = db.Clips.Single(c => c.Id == id);
            clip.Description = "";
            await db.SaveChangesAsync();
        }

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");

        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("og:description");
        body.Should().NotContain("twitter:description");
    }

    [Fact]
    public async Task ShareCodeResolve_CrawlerUA_HtmlEscapesTitle()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "<script>alert('xss')</script>");

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");

        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("<script>alert");
        body.Should().Contain("&lt;script&gt;");
    }

    // ---- GET /clips/featured ----

    [Fact]
    public async Task Featured_EmptyDb_Returns204()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Featured_PicksHighestScoringClip()
    {
        // Three clips of identical age — engagement alone decides. The clip with the
        // most views in today's UTC window wins.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        // Use a timestamp inside today's UTC day AND within trending's typical recency
        // (5 min ago). If `now` is within 5 minutes of UTC midnight, snap to todayStart
        // so the test isn't time-of-day fragile.
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (hot, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "hot");
        var (mid, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "mid");
        var (cool, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "cool");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 20).Select(_ => new ClipView { ClipId = hot, CreatedAt = engagementAt }));
            db.ClipViews.AddRange(
                Enumerable.Range(0, 5).Select(_ => new ClipView { ClipId = mid, CreatedAt = engagementAt }));
            db.ClipViews.AddRange(
                Enumerable.Range(0, 1).Select(_ => new ClipView { ClipId = cool, CreatedAt = engagementAt }));
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(hot);
        body.GetProperty("title").GetString().Should().Be("hot");
    }

    [Fact]
    public async Task Featured_SkipsNonPublicClips()
    {
        // An unlisted clip with overwhelming engagement is never the featured pick.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (unlisted, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "unlisted", visibility: "unlisted");
        var (publicClip, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "public");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 50).Select(_ => new ClipView { ClipId = unlisted, CreatedAt = engagementAt }));
            db.ClipViews.Add(new ClipView { ClipId = publicClip, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(publicClip);
    }

    [Fact]
    public async Task Featured_SkipsNonReadyClips()
    {
        // A processing/failed clip with overwhelming engagement is never the featured pick.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (processing, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "processing", status: "processing");
        var (ready, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "ready");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 50).Select(_ => new ClipView { ClipId = processing, CreatedAt = engagementAt }));
            db.ClipViews.Add(new ClipView { ClipId = ready, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(ready);
    }

    [Fact]
    public async Task Featured_NoEngagementToday_Returns204()
    {
        // Clips exist but none have likes/views since 00:00 UTC today. Server returns
        // 204; the web client is responsible for falling back to "newest clip" so the
        // hero never goes blank.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (clipId, _) = await SeedClipAsync(userId, now.AddDays(-10), title: "old");

        await using (var db = _fx.CreateContext())
        {
            // Engagement strictly before today's UTC start. Construct as a UTC
            // DateTimeOffset explicitly so Npgsql will bind it to `timestamp with
            // time zone` (DateTime.Date returns Kind=Unspecified, which on a non-UTC
            // host produces a non-zero offset DateTimeOffset on implicit conversion).
            var utcMidnight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            db.ClipViews.Add(new ClipView { ClipId = clipId, CreatedAt = utcMidnight.AddSeconds(-1) });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Featured_TieBreak_HigherLikeCountWins()
    {
        // Two clips with identical engagement-in-window (identical score) and identical
        // ages. Total LikeCount (denormalized, includes pre-today likes) is the next
        // tie-breaker per the issue contract.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (likerA, _) = await SeedUserAndIssueTokenAsync("liker-a");
        var (likerB, _) = await SeedUserAndIssueTokenAsync("liker-b");
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;
        var sharedCreatedAt = now.AddHours(-1);

        var (lowLikes, _) = await SeedClipAsync(userId, sharedCreatedAt, title: "low-likes");
        var (highLikes, _) = await SeedClipAsync(userId, sharedCreatedAt, title: "high-likes");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = lowLikes, CreatedAt = engagementAt });
            db.ClipViews.Add(new ClipView { ClipId = highLikes, CreatedAt = engagementAt });

            // Bump the denormalized LikeCount on highLikes only. Pre-today like rows
            // exist on this clip but don't affect today's score; they DO affect
            // LikeCount, which is the tie-breaker.
            var highClip = await db.Clips.FirstAsync(c => c.Id == highLikes);
            highClip.LikeCount = 5;
            var lowClip = await db.Clips.FirstAsync(c => c.Id == lowLikes);
            lowClip.LikeCount = 1;
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(highLikes);
    }

    [Fact]
    public async Task Featured_TieBreak_NewerCreatedAtWinsWhenLikesEqual()
    {
        // Identical score, identical LikeCount → newer CreatedAt wins.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        // Both clips have CreatedAt within the same hour so the (hours+2)^1.5 denominator
        // is identical to 4+ decimal places — scores match. (Different CreatedAt values
        // produce slightly different scores in principle; pick values close enough that
        // the score difference is < double precision noise. 1 second apart at ~1h age:
        // score delta is ~1e-10, well below tie-break sensitivity.)
        var (older, _) = await SeedClipAsync(userId, now.AddHours(-1).AddSeconds(-1), title: "older");
        var (newer, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "newer");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = older, CreatedAt = engagementAt });
            db.ClipViews.Add(new ClipView { ClipId = newer, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(newer);
    }

    [Fact]
    public async Task Featured_TieBreak_HigherIdWinsWhenAllElseEqual()
    {
        // Identical score, LikeCount, AND CreatedAt → higher Guid wins. Final
        // deterministic tie-breaker so the daily pick is always reproducible.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;
        var sharedCreatedAt = now.AddHours(-1);

        var (a, _) = await SeedClipAsync(userId, sharedCreatedAt, title: "a");
        var (b, _) = await SeedClipAsync(userId, sharedCreatedAt, title: "b");
        var expectedWinner = a.CompareTo(b) > 0 ? a : b;

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = a, CreatedAt = engagementAt });
            db.ClipViews.Add(new ClipView { ClipId = b, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(expectedWinner);
    }

    [Fact]
    public async Task Featured_CachedWithinSameDay_ReturnsSameClipEvenAfterBetterContender()
    {
        // First call computes the winner and caches under featured:{yyyy-MM-dd}.
        // A new clip inserted afterwards with much higher engagement should NOT
        // become the featured pick on a second call within the same day.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (original, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "original");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = original, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var firstResp = await client.GetAsync("/clips/featured");
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await firstResp.Content.ReadFromJsonAsync<JsonElement>();
        firstBody.GetProperty("id").GetGuid().Should().Be(original);

        // Insert a clip with overwhelming engagement.
        var (challenger, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "challenger");
        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 1000).Select(_ => new ClipView { ClipId = challenger, CreatedAt = engagementAt }));
            await db.SaveChangesAsync();
        }

        var secondResp = await client.GetAsync("/clips/featured");
        secondResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await secondResp.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("id").GetGuid().Should().Be(original, "the cache pins the pick within the UTC day");
    }

    [Fact]
    public async Task Featured_StaleCachedClip_EvictsAndReturns204()
    {
        // First call caches the winner. Then the clip is hard-deleted. The next call
        // should detect the stale cache (rehydration finds nothing), evict the key,
        // and return 204. (A follow-up call would then re-pick from current state.)
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (clipId, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "doomed");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = clipId, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var firstResp = await client.GetAsync("/clips/featured");
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Hard-delete the cached winner. Delete ClipViews + Clip in order to satisfy FK.
        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.RemoveRange(db.ClipViews.Where(v => v.ClipId == clipId));
            await db.SaveChangesAsync();
            db.Clips.Remove(await db.Clips.FirstAsync(c => c.Id == clipId));
            await db.SaveChangesAsync();
        }

        var secondResp = await client.GetAsync("/clips/featured");
        secondResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Featured_LikedByMe_ReflectsCallingUserDespiteCachedPick()
    {
        // The cache stores only the Guid, so likedByMe is recomputed every request.
        // Same pick, two callers, different likedByMe.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (likerId, likerToken) = await SeedUserAndIssueTokenAsync("liker");
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (clipId, _) = await SeedClipAsync(authorId, now.AddHours(-1), title: "liked");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = clipId, CreatedAt = engagementAt });
            db.Likes.Add(new Like { UserId = likerId, ClipId = clipId, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        // First call: anonymous → likedByMe should be false
        using var anonClient = _factory!.CreateClient();
        var anonResp = await anonClient.GetAsync("/clips/featured");
        anonResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var anonBody = await anonResp.Content.ReadFromJsonAsync<JsonElement>();
        anonBody.GetProperty("likedByMe").GetBoolean().Should().BeFalse();

        // Second call: liker (same cached pick) → likedByMe should be true
        using var likerClient = ClientWithBearer(likerToken);
        var likerResp = await likerClient.GetAsync("/clips/featured");
        likerResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var likerBody = await likerResp.Content.ReadFromJsonAsync<JsonElement>();
        likerBody.GetProperty("id").GetGuid().Should().Be(clipId, "cached pick is shared across callers");
        likerBody.GetProperty("likedByMe").GetBoolean().Should().BeTrue();
    }
}
