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

[Collection("PostgresClips")]
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
        int? gameId = null,
        string? videoCodec = null,
        string? importSourceUrl = null,
        int likeCount = 0,
        int viewCount = 0)
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
            VideoCodec = videoCodec,
            ShareCode = shareCode,
            Status = status,
            Visibility = visibility,
            DurationSecs = 30,
            Width = 1920,
            Height = 1080,
            FileSizeBytes = 1_000_000,
            ImportSourceUrl = importSourceUrl,
            LikeCount = likeCount,
            ViewCount = viewCount,
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
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (a, _) = await SeedClipAsync(userId, now.AddMinutes(-3), title: "oldest-ready");
        var (b, _) = await SeedClipAsync(userId, now.AddMinutes(-2), title: "middle-ready");
        var (c, _) = await SeedClipAsync(userId, now.AddMinutes(-1), title: "newest-ready");
        await SeedClipAsync(userId, now, status: "processing", title: "not-ready");
        await SeedClipAsync(userId, now, visibility: "unlisted", title: "unlisted");
        await SeedClipAsync(userId, now, visibility: "private", title: "private");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(c, b, a);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        // The feed is public-only even for the uploader — private clips live on the
        // owner's profile, never in feeds.
        using var ownerClient = ClientWithBearer(token);
        var ownerResp = await ownerClient.GetAsync("/clips/feed");
        ownerResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ownerBody = await ownerResp.Content.ReadFromJsonAsync<JsonElement>();
        ownerBody.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid())
            .Should().Equal(c, b, a);
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
    public async Task Feed_CachedFirstPage_LikedByMeStaysPerCaller()
    {
        // The first-page anonymous projection is cached and shared; likedByMe must be re-stamped
        // per caller so one user's like never leaks to another through the cache.
        await _fx.ResetAsync();
        var (likerId, likerToken) = await SeedUserAndIssueTokenAsync("liker");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author2");
        var (clipId, _) = await SeedClipAsync(authorId, DateTimeOffset.UtcNow);
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = likerId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Anonymous request first → populates the shared cache entry with likedByMe=false.
        using (var anon = _factory!.CreateClient())
        {
            var anonBody = await (await anon.GetAsync("/clips/feed")).Content.ReadFromJsonAsync<JsonElement>();
            anonBody.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("likedByMe").GetBoolean())
                .Should().OnlyContain(l => l == false);
        }

        // The liker hits the same cached page within TTL but must still see likedByMe=true.
        using (var likerClient = ClientWithBearer(likerToken))
        {
            var likerBody = await (await likerClient.GetAsync("/clips/feed")).Content.ReadFromJsonAsync<JsonElement>();
            likerBody.GetProperty("items").EnumerateArray()
                .Single(e => e.GetProperty("id").GetGuid() == clipId)
                .GetProperty("likedByMe").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task Feed_CachedFirstPage_FilledByAuthedUser_DoesNotLeakToAnother()
    {
        // Symmetric to the test above, guarding the riskier direction: an *authenticated* caller
        // (who liked the clip) warms the cache, then a different user reads the same entry. Because
        // only the anonymous projection is cached, the filler's likedByMe must never leak — a future
        // refactor routing an authed caller through a non-anonymous factory would trip this.
        await _fx.ResetAsync();
        var (likerId, likerToken) = await SeedUserAndIssueTokenAsync("liker-fills");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("other-reads");
        var (clipId, _) = await SeedClipAsync(likerId, DateTimeOffset.UtcNow);
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = likerId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Liker warms the cache via the authenticated path → sees their own like.
        using (var likerClient = ClientWithBearer(likerToken))
        {
            var body = await (await likerClient.GetAsync("/clips/feed")).Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == clipId)
                .GetProperty("likedByMe").GetBoolean().Should().BeTrue();
        }

        // A different user reads the same cached page and must NOT inherit the liker's flag.
        using (var otherClient = ClientWithBearer(otherToken))
        {
            var body = await (await otherClient.GetAsync("/clips/feed")).Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == clipId)
                .GetProperty("likedByMe").GetBoolean().Should().BeFalse();
        }
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

    // ---- GET /clips/feed?gameId= ----

    [Fact]
    public async Task Feed_GameIdFilter_ReturnsOnlyThatGamesClips_AndCachesPerGame()
    {
        // Two different gameId requests are both no-cursor first pages, so each is cached.
        // Asserting valorant then apex return disjoint sets proves both the filter AND that
        // the per-game cache key doesn't leak one game's page into the other's.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        // gameId 2 = valorant, gameId 5 = apex-legends (seeded in the Initial migration).
        var (val1, _) = await SeedClipAsync(userId, now.AddSeconds(-1), title: "val-1", gameId: 2);
        var (val2, _) = await SeedClipAsync(userId, now.AddSeconds(-2), title: "val-2", gameId: 2);
        var (apex1, _) = await SeedClipAsync(userId, now.AddSeconds(-3), title: "apex-1", gameId: 5);
        await SeedClipAsync(userId, now.AddSeconds(-4), title: "no-game");

        using var client = _factory!.CreateClient();

        var valResp = await client.GetAsync("/clips/feed?gameId=2");
        valResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var valIds = (await valResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        valIds.Should().Equal(val1, val2);

        var apexResp = await client.GetAsync("/clips/feed?gameId=5");
        apexResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var apexIds = (await apexResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        apexIds.Should().Equal(apex1);
    }

    [Fact]
    public async Task Feed_GameIdFilter_UnknownGame_ReturnsEmpty()
    {
        // An unknown/non-matching gameId simply matches no clips (empty page), never 400 —
        // same forgive-and-fall-back spirit as the source/cursor handling.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, title: "val", gameId: 2);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?gameId=999999");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Feed_GameIdFilter_ComposesWithFollowingSource()
    {
        // gameId must compose with source=following: the result is the intersection of
        // "followed authors" and "this game", so a followed author's clip for another game
        // and a stranger's clip for this game are both excluded.
        await _fx.ResetAsync();
        var (viewerId, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (strangerId, _) = await SeedUserAndIssueTokenAsync("stranger");
        var now = DateTimeOffset.UtcNow;

        var (authorVal, _) = await SeedClipAsync(authorId, now.AddSeconds(-1), title: "author-val", gameId: 2);
        await SeedClipAsync(authorId, now.AddSeconds(-2), title: "author-apex", gameId: 5);
        await SeedClipAsync(strangerId, now.AddSeconds(-3), title: "stranger-val", gameId: 2);

        await using (var db = _fx.CreateContext())
        {
            db.Follows.Add(new Follow { FollowerId = viewerId, FolloweeId = authorId, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(viewerToken);
        var resp = await client.GetAsync("/clips/feed?source=following&gameId=2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(authorVal);
    }

    [Fact]
    public async Task Feed_GameIdFilter_ComposesWithTrendingSort()
    {
        // gameId must also narrow the trending path, whose ranked result is cached under a
        // game-suffixed key. Both clips have equal engagement in the window, so without the
        // filter both would rank; ?gameId=2 must return only the valorant clip.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (val, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "val-hot", gameId: 2);
        var (apex, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "apex-hot", gameId: 5);

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 5).Select(_ => new ClipView { ClipId = val, CreatedAt = now.AddMinutes(-5) }));
            db.ClipViews.AddRange(
                Enumerable.Range(0, 5).Select(_ => new ClipView { ClipId = apex, CreatedAt = now.AddMinutes(-5) }));
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=trending&window=24h&gameId=2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(val);
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

    // ---- GET /clips/feed?sort=top ----

    [Fact]
    public async Task Top_OrdersByLikeCountDesc()
    {
        // "Top" ranks by the denormalized like_count. Views/recency only break ties, so with
        // distinct like counts the order is purely likes-descending regardless of age.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        // Oldest clip has the most likes: proves likes beat recency.
        var (most, _) = await SeedClipAsync(userId, now.AddHours(-3), title: "most", likeCount: 10);
        var (mid, _) = await SeedClipAsync(userId, now.AddHours(-2), title: "mid", likeCount: 5);
        var (least, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "least", likeCount: 1);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=top&window=24h");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(most, mid, least);
    }

    [Fact]
    public async Task Top_TiesBrokenByViewsThenRecency()
    {
        // Equal like counts: higher view_count wins; on equal views the newer clip wins.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (moreViews, _) = await SeedClipAsync(userId, now.AddHours(-3), title: "more-views", likeCount: 5, viewCount: 100);
        var (newer, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "newer", likeCount: 5, viewCount: 10);
        var (older, _) = await SeedClipAsync(userId, now.AddHours(-2), title: "older", likeCount: 5, viewCount: 10);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=top&window=24h");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        // moreViews (views tiebreak) → newer, then older (recency tiebreak on equal likes+views).
        ids.Should().Equal(moreViews, newer, older);
    }

    [Fact]
    public async Task Top_WindowFiltersByClipCreationTime()
    {
        // The window bounds the candidate set by created_at (Reddit "Top: this week" model): a
        // highly-liked clip created outside the window is excluded, however many likes it has.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (recent, _) = await SeedClipAsync(userId, now.AddHours(-2), title: "recent", likeCount: 1);
        var (old, _) = await SeedClipAsync(userId, now.AddDays(-3), title: "old-but-loved", likeCount: 999);

        using var client = _factory!.CreateClient();

        var dayIds = (await (await client.GetAsync("/clips/feed?sort=top&window=24h")).Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        dayIds.Should().Equal(recent);

        // 7d widens the window to admit the 3-day-old clip, which then outranks on likes.
        var weekIds = (await (await client.GetAsync("/clips/feed?sort=top&window=7d")).Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        weekIds.Should().Equal(old, recent);
    }

    [Fact]
    public async Task Top_AllWindow_IncludesArbitrarilyOldClips()
    {
        // window=all applies no created_at bound, so a year-old clip is eligible.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (ancient, _) = await SeedClipAsync(userId, now.AddDays(-400), title: "ancient", likeCount: 3);

        using var client = _factory!.CreateClient();

        var dayIds = (await (await client.GetAsync("/clips/feed?sort=top&window=24h")).Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        dayIds.Should().BeEmpty();

        var allIds = (await (await client.GetAsync("/clips/feed?sort=top&window=all")).Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        allIds.Should().Equal(ancient);
    }

    [Fact]
    public async Task Top_ExcludesNonPublicAndNonReady()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (publicReady, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "public-ready", likeCount: 5);
        await SeedClipAsync(userId, now.AddHours(-1), visibility: "unlisted", title: "unlisted", likeCount: 50);
        await SeedClipAsync(userId, now.AddHours(-1), status: "processing", title: "processing", likeCount: 50);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=top&window=24h");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

        ids.Should().Equal(publicReady);
    }

    [Fact]
    public async Task Top_IncludesZeroLikeClips_WithinWindow()
    {
        // Unlike trending (which requires engagement in the window), "top" is a ranking of all
        // public clips in the window — a brand-new clip with no likes still appears, ranked last.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (liked, _) = await SeedClipAsync(userId, now.AddHours(-2), title: "liked", likeCount: 3);
        var (unliked, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "unliked", likeCount: 0);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=top&window=24h");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

        ids.Should().Equal(liked, unliked);
    }

    [Fact]
    public async Task Top_PaginationKeyset_DrainsAllInRankOrderWithoutSkips()
    {
        // Keyset pagination over the (like_count, view_count, created_at, id) ranking tuple must
        // walk the full ordered result across pages with no dupes and no skips — the same contract
        // the latest feed's (created_at, id) cursor provides.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var expected = new List<Guid>();
        // Distinct like counts 5..1 so the expected rank order is unambiguous.
        foreach (var likes in new[] { 5, 4, 3, 2, 1 })
        {
            var (id, _) = await SeedClipAsync(userId, now.AddMinutes(-likes), title: $"likes-{likes}", likeCount: likes);
            expected.Add(id);
        }

        using var client = _factory!.CreateClient();
        var drained = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var url = $"/clips/feed?sort=top&window=24h&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var body = await (await client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();
            drained.AddRange(body.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
            cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null : body.GetProperty("nextCursor").GetString();
            if (cursor is null) break;
        }

        drained.Should().Equal(expected);
    }

    [Fact]
    public async Task Top_PaginationKeyset_EqualLikeCounts_NoSkipsAcrossPages()
    {
        // The hard case: every clip shares the same like_count, so paging relies entirely on the
        // view_count/created_at/id tiebreaks travelling in the cursor. A like_count-only cursor
        // would skip or repeat rows at each page boundary.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var expected = new List<Guid>();
        foreach (var views in new[] { 40, 30, 20, 10 })
        {
            var (id, _) = await SeedClipAsync(userId, now.AddMinutes(-views), title: $"v{views}", likeCount: 7, viewCount: views);
            expected.Add(id);
        }

        using var client = _factory!.CreateClient();
        var drained = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var url = $"/clips/feed?sort=top&window=24h&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var body = await (await client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();
            drained.AddRange(body.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
            cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null : body.GetProperty("nextCursor").GetString();
            if (cursor is null) break;
        }

        drained.Should().Equal(expected);
    }

    [Fact]
    public async Task Top_InvalidWindow_Returns400()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed?sort=top&window=bogus");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Top_MissingWindow_Returns400()
    {
        // Like trending, "top" is meaningless without a window — a missing value is a 400 rather
        // than a silent default so a UI bug surfaces instead of an arbitrary ranking window.
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed?sort=top");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Top_EmptyResult_Returns200WithNoItems()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed?sort=top&window=all");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Top_30dWindow_IsAccepted()
    {
        // Coverage for the 30d window value the Home/Trending window tabs use.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (inside, _) = await SeedClipAsync(userId, now.AddDays(-20), title: "inside-30d", likeCount: 2);
        await SeedClipAsync(userId, now.AddDays(-45), title: "outside-30d", likeCount: 99);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/feed?sort=top&window=30d");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(inside);
    }

    // ---- GET /clips/feed?source=for-you ----

    // me follows `followed`; me has liked a clip in game 2 (Valorant). Returns the ids of the
    // three seeded authors plus me's token so each test can assert tier placement.
    private async Task<(Guid me, string token, Guid followed, Guid stranger)> SeedForYouSignalsAsync()
    {
        var (me, token) = await SeedUserAndIssueTokenAsync("reader");
        var (followed, _) = await SeedUserAndIssueTokenAsync("followed");
        var (stranger, _) = await SeedUserAndIssueTokenAsync("stranger");

        // Establish liked-game {2}: me likes an (unlisted, so it never appears in the feed
        // itself) clip in game 2. The liked-game signal ignores the liked clip's visibility.
        var (likeSeed, _) = await SeedClipAsync(
            stranger, DateTimeOffset.UtcNow, visibility: "unlisted", title: "like-seed", gameId: 2);
        await using (var db = _fx.CreateContext())
        {
            db.Follows.Add(new Follow { FollowerId = me, FolloweeId = followed, CreatedAt = DateTimeOffset.UtcNow });
            db.Likes.Add(new Like { UserId = me, ClipId = likeSeed, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        return (me, token, followed, stranger);
    }

    private static List<Guid> FeedIds(JsonElement body) =>
        body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task ForYou_Anonymous_IdenticalToLatest()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, now.AddMinutes(-1), title: "c1");
        await SeedClipAsync(userId, now.AddMinutes(-2), title: "c2");
        await SeedClipAsync(userId, now.AddMinutes(-3), title: "c3");

        using var client = _factory!.CreateClient();
        var forYou = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");
        var latest = await client.GetFromJsonAsync<JsonElement>("/clips/feed");

        FeedIds(forYou).Should().Equal(FeedIds(latest));
    }

    [Fact]
    public async Task ForYou_AuthenticatedColdStart_IdenticalToLatest()
    {
        await _fx.ResetAsync();
        var (me, token) = await SeedUserAndIssueTokenAsync("reader");
        var (author, _) = await SeedUserAndIssueTokenAsync("author");
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(author, now.AddMinutes(-1), title: "c1");
        await SeedClipAsync(author, now.AddMinutes(-2), title: "c2");

        using var client = ClientWithBearer(token);
        var forYou = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");
        var latest = await client.GetFromJsonAsync<JsonElement>("/clips/feed");

        FeedIds(forYou).Should().Equal(FeedIds(latest));
    }

    [Fact]
    public async Task ForYou_TierOrdering_FollowBeatsLikedGameBeatsBackfill_RegardlessOfRecency()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        // Tier 0 is the OLDEST clip, tier 2 the NEWEST — proves tier dominates recency.
        var (t0, _) = await SeedClipAsync(followed, now.AddMinutes(-10), title: "t0-follow");
        var (t1, _) = await SeedClipAsync(stranger, now.AddMinutes(-5), title: "t1-liked-game", gameId: 2);
        var (t2, _) = await SeedClipAsync(stranger, now.AddMinutes(-1), title: "t2-backfill");

        using var client = ClientWithBearer(token);
        var body = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");

        FeedIds(body).Should().Equal(t0, t1, t2);
    }

    [Fact]
    public async Task ForYou_FollowedAuthorInLikedGame_AppearsOnceInTier0()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        // Followed author posts in the liked game (game 2). It must appear once, in tier 0 —
        // so it ranks above a stranger's NEWER liked-game (tier 1) clip.
        var (dual, _) = await SeedClipAsync(followed, now.AddMinutes(-5), title: "dual", gameId: 2);
        var (strangerLikedGame, _) = await SeedClipAsync(stranger, now.AddMinutes(-1), title: "t1", gameId: 2);

        using var client = ClientWithBearer(token);
        var ids = FeedIds(await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you"));

        ids.Count(id => id == dual).Should().Be(1);
        ids.Should().Equal(dual, strangerLikedGame);
    }

    [Fact]
    public async Task ForYou_LikedGameInference_SurfacesOtherClipsInThatGameInTier1()
    {
        await _fx.ResetAsync();
        var (me, token) = await SeedUserAndIssueTokenAsync("reader");
        var (stranger, _) = await SeedUserAndIssueTokenAsync("stranger");
        var now = DateTimeOffset.UtcNow;
        // me likes a clip in game 3; NO follows. A different clip in game 3 must surface in tier 1
        // above a newer backfill clip.
        var (liked, _) = await SeedClipAsync(stranger, now.AddMinutes(-9), title: "liked", gameId: 3);
        var (sameGame, _) = await SeedClipAsync(stranger, now.AddMinutes(-5), title: "same-game", gameId: 3);
        var (backfill, _) = await SeedClipAsync(stranger, now.AddMinutes(-1), title: "backfill");
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = me, ClipId = liked, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var ids = FeedIds(await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you"));

        // liked + sameGame are both game 3 (tier 1, newest-first); backfill is tier 2.
        ids.Should().Equal(sameGame, liked, backfill);
    }

    [Fact]
    public async Task ForYou_GameIdFilter_NarrowsEveryTierToThatGame_PreservingTierOrder()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        // The game pills must narrow the personalised feed just like the latest path does.
        // Tier order still dominates recency *within* the filtered game: the followed author's
        // game-2 clip (tier 0, older) leads the stranger's game-2 clip (tier 1, newer).
        var (faVal, _) = await SeedClipAsync(followed, now.AddMinutes(-9), title: "fa-val", gameId: 2);
        var (strVal, _) = await SeedClipAsync(stranger, now.AddMinutes(-1), title: "str-val", gameId: 2);
        // Off-game clips that would otherwise rank (tier 0 / tier 1) must be excluded by ?gameId=2.
        var (faApex, _) = await SeedClipAsync(followed, now, title: "fa-apex", gameId: 5);
        var (strBackfill, _) = await SeedClipAsync(stranger, now, title: "str-backfill");

        using var client = ClientWithBearer(token);
        var ids = FeedIds(await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you&gameId=2"));

        ids.Should().Equal(faVal, strVal);
        ids.Should().NotContain(faApex).And.NotContain(strBackfill);
    }

    [Fact]
    public async Task ForYou_CrossTierPageFill_SpansBoundaryAndResumesViaCursor()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        // 2 per tier, newest-first within tier.
        var (t0a, _) = await SeedClipAsync(followed, now.AddMinutes(-1), title: "t0a");
        var (t0b, _) = await SeedClipAsync(followed, now.AddMinutes(-2), title: "t0b");
        var (t1a, _) = await SeedClipAsync(stranger, now.AddMinutes(-3), title: "t1a", gameId: 2);
        var (t1b, _) = await SeedClipAsync(stranger, now.AddMinutes(-4), title: "t1b", gameId: 2);
        var (t2a, _) = await SeedClipAsync(stranger, now.AddMinutes(-5), title: "t2a");
        var (t2b, _) = await SeedClipAsync(stranger, now.AddMinutes(-6), title: "t2b");

        using var client = ClientWithBearer(token);
        var page1 = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you&limit=3");
        FeedIds(page1).Should().Equal(t0a, t0b, t1a);
        var cursor = page1.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        var page2 = await client.GetFromJsonAsync<JsonElement>(
            $"/clips/feed?source=for-you&limit=3&cursor={Uri.EscapeDataString(cursor!)}");
        FeedIds(page2).Should().Equal(t1b, t2a, t2b);
        page2.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ForYou_DeepPagination_LimitOne_WalksEveryTierAndDrains()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        var (t0, _) = await SeedClipAsync(followed, now.AddMinutes(-1), title: "t0");
        var (t1, _) = await SeedClipAsync(stranger, now.AddMinutes(-2), title: "t1", gameId: 2);
        var (t2, _) = await SeedClipAsync(stranger, now.AddMinutes(-3), title: "t2");
        var expected = new[] { t0, t1, t2 };

        using var client = ClientWithBearer(token);
        var collected = new List<Guid>();
        string? cursor = null;
        for (var i = 0; i < 10; i++) // safety bound; real loop breaks on null cursor
        {
            var url = cursor is null
                ? "/clips/feed?source=for-you&limit=1"
                : $"/clips/feed?source=for-you&limit=1&cursor={Uri.EscapeDataString(cursor)}";
            var body = await client.GetFromJsonAsync<JsonElement>(url);
            collected.AddRange(FeedIds(body));
            var next = body.GetProperty("nextCursor");
            if (next.ValueKind == JsonValueKind.Null) break;
            cursor = next.GetString();
        }

        collected.Should().Equal(expected); // no repeats, no skips, drained across tier boundaries
    }

    [Fact]
    public async Task ForYou_LikedByMe_IsPerCaller()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var (other, otherToken) = await SeedUserAndIssueTokenAsync("other");
        var (clip, _) = await SeedClipAsync(followed, DateTimeOffset.UtcNow.AddMinutes(-1), title: "clip");
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = me, ClipId = clip, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var meClient = ClientWithBearer(token);
        using var otherClient = ClientWithBearer(otherToken);
        var meBody = await meClient.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");
        var otherBody = await otherClient.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");

        bool LikedByMe(JsonElement body, Guid id) => body.GetProperty("items").EnumerateArray()
            .First(e => e.GetProperty("id").GetGuid() == id).GetProperty("likedByMe").GetBoolean();

        LikedByMe(meBody, clip).Should().BeTrue();
        // `other` has no follows/likes → cold-start → served the latest path; the clip still
        // appears (global latest) with likedByMe=false.
        LikedByMe(otherBody, clip).Should().BeFalse();
    }

    [Fact]
    public async Task ForYou_SignalGainedBetweenPages_RestartsPersonalisedRankingGracefully()
    {
        await _fx.ResetAsync();
        var (me, token) = await SeedUserAndIssueTokenAsync("reader");
        var (author, _) = await SeedUserAndIssueTokenAsync("author");
        var (stranger, _) = await SeedUserAndIssueTokenAsync("stranger");
        var now = DateTimeOffset.UtcNow;
        // No signals yet → cold-start serves the plain latest ordering, newest-first.
        var (b1, _) = await SeedClipAsync(stranger, now.AddMinutes(-1), title: "b1");
        var (b2, _) = await SeedClipAsync(stranger, now.AddMinutes(-2), title: "b2");
        await SeedClipAsync(stranger, now.AddMinutes(-3), title: "b3");
        // A followed-author clip OLDER than every backfill clip: only a tier-0 restart (not
        // recency) can surface it at the front of page 2.
        var (fa, _) = await SeedClipAsync(author, now.AddMinutes(-10), title: "followed");

        using var client = ClientWithBearer(token);

        // Page 1: cold-start → latest ordering + a plain KeysetCursor.
        var page1 = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you&limit=2");
        FeedIds(page1).Should().Equal(b1, b2);
        var cursor = page1.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        // Caller follows an author between the two page requests.
        await using (var db = _fx.CreateContext())
        {
            db.Follows.Add(new Follow { FollowerId = me, FolloweeId = author, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        // Page 2 replays the page-1 cursor, but the caller now has signals, so the feed re-ranks
        // into tiers. The plain (cross-type) cursor is intentionally NOT honoured for continuation:
        // paging restarts at tier 0 — gracefully (200, no error), so the followed author leads
        // despite being the oldest clip. Repeating page-1 backfill (b1) is the accepted cost of a
        // mid-session re-rank; a stable session never crosses cursor types.
        var page2 = await client.GetFromJsonAsync<JsonElement>(
            $"/clips/feed?source=for-you&limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        FeedIds(page2).Should().Equal(fa, b1);
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
    public async Task Detail_Private_OwnerReturns200()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("privowner");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "private");

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("visibility").GetString().Should().Be("private");
    }

    [Fact]
    public async Task Detail_Private_OtherUserReturns404()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("privowner");
        var (_, strangerToken) = await SeedUserAndIssueTokenAsync("stranger");
        var (clipId, _) = await SeedClipAsync(ownerId, DateTimeOffset.UtcNow, visibility: "private");

        using var client = ClientWithBearer(strangerToken);
        var resp = await client.GetAsync($"/clips/{clipId}");

        // Same 404 as a nonexistent clip — knowing the id must not confirm anything.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_Private_AnonymousReturns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("privowner");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "private");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_Hidden_OwnerReturns200()
    {
        // Hidden mirrors private on the owner-scoped read paths: the owner can still
        // inspect their own moderated clip.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("hiddenowner");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "hidden");

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("visibility").GetString().Should().Be("hidden");
    }

    [Fact]
    public async Task Detail_Hidden_OtherUserReturns404()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("hiddenowner");
        var (_, strangerToken) = await SeedUserAndIssueTokenAsync("stranger");
        var (clipId, _) = await SeedClipAsync(ownerId, DateTimeOffset.UtcNow, visibility: "hidden");

        using var client = ClientWithBearer(strangerToken);
        var resp = await client.GetAsync($"/clips/{clipId}");

        // Moderator-hidden content must actually be down for link-holders, not just feeds.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_Hidden_AnonymousReturns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("hiddenowner");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "hidden");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
        // No codec recorded for this seed → null; the player plays the master directly.
        body.GetProperty("videoCodec").ValueKind.Should().Be(JsonValueKind.Null);

        var expiresAt = body.GetProperty("videoUrlExpiresAt").GetDateTimeOffset();
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(2));

        // Expiry passed to the storage service should be roughly one hour.
        _storage.Received(1).GetPresignedGetUrl(
            Arg.Any<string>(),
            $"{userId}/{clipId}.mp4",
            Arg.Is<TimeSpan?>(ts => ts.HasValue && ts.Value == TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task Detail_ImportedClip_ReturnsImportSourceUrl()
    {
        // Imported clips carry the original URL on Clip.ImportSourceUrl. The detail endpoint
        // must expose it so the web "Imported from {host}" attribution badge can render.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("importer");
        var (clipId, _) = await SeedClipAsync(
            userId, DateTimeOffset.UtcNow,
            importSourceUrl: "https://www.youtube.com/watch?v=abc123");

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://minio.local/x");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("importSourceUrl").GetString()
            .Should().Be("https://www.youtube.com/watch?v=abc123");
    }

    [Fact]
    public async Task Detail_DirectUpload_ReturnsNullImportSourceUrl()
    {
        // Direct-upload clips never set ImportSourceUrl. The detail response field must be
        // present (so the front-end's null-guard works) and explicitly null — JSON `null`,
        // not an omitted property.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("uploader");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://minio.local/x");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("importSourceUrl", out var prop).Should().BeTrue();
        prop.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Detail_WithVideoCodec_ReturnsCodec()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("av1owner");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, videoCodec: "av1");

        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://minio.local/clips/presigned?sig=abc");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // videoCodec drives the player's native-vs-JIT decision.
        body.GetProperty("videoCodec").GetString().Should().Be("av1");
    }

    [Fact]
    public async Task Stream_CacheMiss_Returns202_AndEnqueuesJob()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("streamer");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);
        // GetObjectMetadataAsync defaults to null on the substitute → cache miss.

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}/stream");

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("pending");
        // A "pending" response must not carry an hlsUrl — lock the 202 payload shape.
        body.GetProperty("hlsUrl").ValueKind.Should().Be(JsonValueKind.Null);

        await using var db = _fx.CreateContext();
        var enqueued = await db.ClipStreamJobs.AsNoTracking().AnyAsync(j => j.ClipId == clipId);
        enqueued.Should().BeTrue();
    }

    [Fact]
    public async Task Stream_CacheHit_Returns200_WithPublicHlsUrl()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("streamer2");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadata(42, "application/vnd.apple.mpegurl"));

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}/stream");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ready");
        var hlsUrl = body.GetProperty("hlsUrl").GetString();
        hlsUrl.Should().NotBeNullOrEmpty();
        hlsUrl.Should().EndWith($"{clipId:N}/master.m3u8");
        hlsUrl.Should().NotContain("sig=");
    }

    [Fact]
    public async Task Stream_H264Master_Returns400_NoJobEnqueued()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("h264streamer");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, videoCodec: "h264");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}/stream");

        // H.264 masters play directly — /stream must refuse rather than queue a pointless transcode.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var db = _fx.CreateContext();
        (await db.ClipStreamJobs.AsNoTracking().AnyAsync(j => j.ClipId == clipId)).Should().BeFalse();
    }

    [Fact]
    public async Task Stream_UnknownClip_Returns404()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{Guid.NewGuid()}/stream");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stream_Private_OwnerReturns202()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("privstreamer");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "private");

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync($"/clips/{clipId}/stream");

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Stream_Private_NonOwnerReturns404_NoJobEnqueued()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("privstreamer");
        var (_, strangerToken) = await SeedUserAndIssueTokenAsync("stranger");
        var (clipId, _) = await SeedClipAsync(ownerId, DateTimeOffset.UtcNow, visibility: "private");

        using var anonymous = _factory!.CreateClient();
        using var stranger = ClientWithBearer(strangerToken);
        var anonResp = await anonymous.GetAsync($"/clips/{clipId}/stream");
        var strangerResp = await stranger.GetAsync($"/clips/{clipId}/stream");

        anonResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        strangerResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = _fx.CreateContext();
        (await db.ClipStreamJobs.AsNoTracking().AnyAsync(j => j.ClipId == clipId)).Should().BeFalse();
    }

    [Fact]
    public async Task Stream_Hidden_OwnerReturns404_NoJobEnqueued()
    {
        // /stream refuses hidden clips for everyone — an owner re-watch would recreate the
        // anonymous HLS a takedown purged, so the hide-time purge stays durable.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("hiddenstreamer");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "hidden");

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync($"/clips/{clipId}/stream");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = _fx.CreateContext();
        (await db.ClipStreamJobs.AsNoTracking().AnyAsync(j => j.ClipId == clipId)).Should().BeFalse();
    }

    [Fact]
    public async Task Stream_Hidden_NonOwnerReturns404_NoJobEnqueued()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("hiddenstreamer");
        var (_, strangerToken) = await SeedUserAndIssueTokenAsync("stranger");
        var (clipId, _) = await SeedClipAsync(ownerId, DateTimeOffset.UtcNow, visibility: "hidden");

        using var anonymous = _factory!.CreateClient();
        using var stranger = ClientWithBearer(strangerToken);
        var anonResp = await anonymous.GetAsync($"/clips/{clipId}/stream");
        var strangerResp = await stranger.GetAsync($"/clips/{clipId}/stream");

        anonResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        strangerResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = _fx.CreateContext();
        (await db.ClipStreamJobs.AsNoTracking().AnyAsync(j => j.ClipId == clipId)).Should().BeFalse();
    }

    [Fact]
    public async Task Stream_FailedJob_Returns503()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("streamer3");
        var (clipId, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow);
        await using (var db = _fx.CreateContext())
        {
            db.ClipStreamJobs.Add(new ClipStreamJob
            {
                ClipId = clipId,
                Status = ClipStreamJobStatuses.Failed,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/clips/{clipId}/stream");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
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
    public async Task ShareCodeResolve_Private_OwnerReturns200()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("privowner");
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "private");

        using var client = ClientWithBearer(token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ShareCodeResolve_Private_AnonymousReturns404()
    {
        // Unlike unlisted, holding the share link is not enough for a private clip.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("privowner");
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "private");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ShareCodeResolve_Private_CrawlerGets404NoOgHtml()
    {
        // Crawlers are anonymous, so a private clip must never leak OG metadata to link previews.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("privowner");
        var (_, shareCode) = await SeedClipAsync(
            userId, DateTimeOffset.UtcNow, visibility: "private", title: "Secret Clip");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("Secret Clip");
    }

    [Fact]
    public async Task ShareCodeResolve_Hidden_OwnerReturns200()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("hiddenowner");
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "hidden");

        using var client = ClientWithBearer(token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ShareCodeResolve_Hidden_AnonymousReturns404()
    {
        // Hiding abusive content must take it down for share-link holders too.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("hiddenowner");
        var (_, shareCode) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, visibility: "hidden");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ShareCodeResolve_Hidden_CrawlerGets404NoOgHtml()
    {
        // A hidden clip must never leak OG metadata to link previews.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("hiddenowner");
        var (_, shareCode) = await SeedClipAsync(
            userId, DateTimeOffset.UtcNow, visibility: "hidden", title: "Moderated Clip");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("Moderated Clip");
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
