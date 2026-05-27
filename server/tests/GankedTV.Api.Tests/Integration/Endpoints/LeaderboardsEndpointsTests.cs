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
public class LeaderboardsEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public LeaderboardsEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    // ---- GET /leaderboards ----

    [Fact]
    public async Task Global_Empty_Returns200WithEmptyArrays()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/leaderboards?window=week");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("window").GetString().Should().Be("week");
        body.GetProperty("topClips").GetArrayLength().Should().Be(0);
        body.GetProperty("topGames").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Global_OmittedWindow_DefaultsToWeek()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/leaderboards");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("window").GetString().Should().Be("week");
    }

    [Fact]
    public async Task Global_InvalidWindow_Returns400()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/leaderboards?window=mounth");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Global_RanksClipsByWindowedLikesNotAllTimeLikeCount()
    {
        // Clip A: 5 old likes (outside window) + 1 fresh like (in window)
        // Clip B: 3 fresh likes (in window)
        // All-time LikeCount would put A on top (6 vs 3); windowed ranking must put B first.
        await _fx.ResetAsync();
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var gameId = await GetGameIdBySlugAsync("valorant");
        var now = DateTimeOffset.UtcNow;
        var (a, _) = await SeedClipAsync(authorId, now.AddDays(-10), gameId, "clipA");
        var (b, _) = await SeedClipAsync(authorId, now.AddDays(-1), gameId, "clipB");

        var oldStamp = now.AddDays(-30); // outside week
        var freshStamp = now.AddHours(-1); // inside week
        await SeedLikesAsync(a, oldStamp, 5);
        await SeedLikesAsync(a, freshStamp, 1);
        await SeedLikesAsync(b, freshStamp, 3);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/leaderboards?window=week");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entries = body.GetProperty("topClips").EnumerateArray().ToList();
        entries.Should().HaveCount(2);
        entries[0].GetProperty("clip").GetProperty("id").GetGuid().Should().Be(b);
        entries[0].GetProperty("rank").GetInt32().Should().Be(1);
        entries[0].GetProperty("windowLikes").GetInt32().Should().Be(3);
        entries[1].GetProperty("clip").GetProperty("id").GetGuid().Should().Be(a);
        entries[1].GetProperty("rank").GetInt32().Should().Be(2);
        entries[1].GetProperty("windowLikes").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Global_OnlyPublicReadyClipsAppear()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var gameId = await GetGameIdBySlugAsync("valorant");
        var now = DateTimeOffset.UtcNow;
        var freshStamp = now.AddHours(-1);
        var (publicReady, _) = await SeedClipAsync(authorId, now, gameId, "good");
        var (processing, _) = await SeedClipAsync(authorId, now, gameId, "processing", status: "processing");
        var (unlisted, _) = await SeedClipAsync(authorId, now, gameId, "unlisted", visibility: "unlisted");

        await SeedLikesAsync(publicReady, freshStamp, 2);
        await SeedLikesAsync(processing, freshStamp, 5);
        await SeedLikesAsync(unlisted, freshStamp, 5);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/leaderboards?window=week");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("topClips").EnumerateArray()
            .Select(e => e.GetProperty("clip").GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(publicReady);
    }

    [Fact]
    public async Task Global_TiesBreakOnNewerThenById_DeterministicallyAndStably()
    {
        // Three clips with identical windowed like counts. Newer createdAt outranks older;
        // identical createdAt falls back to Guid ascending. Running the request twice must
        // produce the same order (stability), and the documented tiebreak must hold.
        await _fx.ResetAsync();
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var gameId = await GetGameIdBySlugAsync("valorant");
        var now = DateTimeOffset.UtcNow;
        var shared = now.AddHours(-3);
        var (older, _) = await SeedClipAsync(authorId, now.AddHours(-5), gameId, "older");
        var (sameA, _) = await SeedClipAsync(authorId, shared, gameId, "sameA");
        var (sameB, _) = await SeedClipAsync(authorId, shared, gameId, "sameB");

        var freshStamp = now.AddMinutes(-10);
        await SeedLikesAsync(older, freshStamp, 2);
        await SeedLikesAsync(sameA, freshStamp, 2);
        await SeedLikesAsync(sameB, freshStamp, 2);

        using var client = _factory!.CreateClient();
        var resp1 = await client.GetAsync("/leaderboards?window=week");
        var ids1 = (await resp1.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("topClips").EnumerateArray()
            .Select(e => e.GetProperty("clip").GetProperty("id").GetGuid()).ToList();

        var resp2 = await client.GetAsync("/leaderboards?window=week");
        var ids2 = (await resp2.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("topClips").EnumerateArray()
            .Select(e => e.GetProperty("clip").GetProperty("id").GetGuid()).ToList();

        ids2.Should().Equal(ids1);
        // First two slots are the shared-createdAt pair, ordered by Guid asc; older is last.
        var expectedSamePair = new[] { sameA, sameB }.OrderBy(g => g).ToList();
        ids1.Take(2).Should().Equal(expectedSamePair);
        ids1[2].Should().Be(older);
    }

    [Fact]
    public async Task Global_TopGamesRanksByWindowedLikesSummedAcrossClips()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var valorant = await GetGameIdBySlugAsync("valorant");
        var apex = await GetGameIdBySlugAsync("apex-legends");
        var now = DateTimeOffset.UtcNow;
        var fresh = now.AddHours(-1);
        var (valClip1, _) = await SeedClipAsync(authorId, now, valorant, "v1");
        var (valClip2, _) = await SeedClipAsync(authorId, now, valorant, "v2");
        var (apexClip, _) = await SeedClipAsync(authorId, now, apex, "a1");

        await SeedLikesAsync(valClip1, fresh, 2);
        await SeedLikesAsync(valClip2, fresh, 2);
        await SeedLikesAsync(apexClip, fresh, 3);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/leaderboards?window=week");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var games = body.GetProperty("topGames").EnumerateArray().ToList();

        games.Should().HaveCount(2);
        games[0].GetProperty("game").GetProperty("slug").GetString().Should().Be("valorant");
        games[0].GetProperty("windowLikes").GetInt32().Should().Be(4);
        games[0].GetProperty("rank").GetInt32().Should().Be(1);
        games[1].GetProperty("game").GetProperty("slug").GetString().Should().Be("apex-legends");
        games[1].GetProperty("windowLikes").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Global_AllWindowIncludesOldLikesThatWeekExcludes()
    {
        // Same clip, one ancient like. `week` rejects it; `all` includes it.
        await _fx.ResetAsync();
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var gameId = await GetGameIdBySlugAsync("valorant");
        var (clipId, _) = await SeedClipAsync(authorId, DateTimeOffset.UtcNow.AddDays(-90), gameId, "ancient");
        await SeedLikesAsync(clipId, DateTimeOffset.UtcNow.AddDays(-90), 1);

        using var client = _factory!.CreateClient();
        var weekBody = await (await client.GetAsync("/leaderboards?window=week")).Content.ReadFromJsonAsync<JsonElement>();
        weekBody.GetProperty("topClips").GetArrayLength().Should().Be(0);

        var allBody = await (await client.GetAsync("/leaderboards?window=all")).Content.ReadFromJsonAsync<JsonElement>();
        allBody.GetProperty("topClips").GetArrayLength().Should().Be(1);
        allBody.GetProperty("topClips")[0].GetProperty("clip").GetProperty("id").GetGuid().Should().Be(clipId);
    }

    // ---- GET /games/{slug}/leaderboard ----

    [Fact]
    public async Task GameLeaderboard_UnknownSlug_Returns404()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games/does-not-exist/leaderboard?window=week");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GameLeaderboard_InvalidWindow_Returns400()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games/valorant/leaderboard?window=fortnight");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GameLeaderboard_EmptyWindow_Returns200WithEmptyEntries()
    {
        // Known game, no likes in week → empty entries, NOT 404.
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games/valorant/leaderboard?window=week");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("game").GetProperty("slug").GetString().Should().Be("valorant");
        body.GetProperty("entries").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GameLeaderboard_ScopesToSelectedGameOnly()
    {
        // A more-liked clip in a different game must not appear in valorant's board.
        await _fx.ResetAsync();
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var valorant = await GetGameIdBySlugAsync("valorant");
        var apex = await GetGameIdBySlugAsync("apex-legends");
        var now = DateTimeOffset.UtcNow;
        var fresh = now.AddHours(-1);
        var (valClip, _) = await SeedClipAsync(authorId, now, valorant, "v");
        var (apexClip, _) = await SeedClipAsync(authorId, now, apex, "a");
        await SeedLikesAsync(valClip, fresh, 1);
        await SeedLikesAsync(apexClip, fresh, 9);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/valorant/leaderboard?window=week");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("clip").GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(valClip);
    }

    [Fact]
    public async Task Global_Cached_LikedByMeStaysPerCaller()
    {
        // The leaderboard payload is cached anonymously and re-stamped per caller, mirroring
        // the trending cache. The high-risk regression is the cache leaking one caller's
        // likedByMe to another. Exercise both directions: anonymous-warms-then-authed and
        // authed-warms-then-anonymous, since per-caller stamping must hold regardless of
        // which request populated the cache.
        await _fx.ResetAsync();
        var (likerId, likerToken) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "liker");
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var gameId = await GetGameIdBySlugAsync("valorant");
        var (clipId, _) = await SeedClipAsync(authorId, DateTimeOffset.UtcNow, gameId, "lb-cached");
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = likerId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) });
            await db.SaveChangesAsync();
        }

        // Authed request warms the cache; the stamped result must NOT leak likedByMe=true
        // back into the shared cache entry.
        using (var likerClient = AuthTestHelpers.CreateBearerClient(_factory!, likerToken))
        {
            var body = await (await likerClient.GetAsync("/leaderboards?window=week")).Content.ReadFromJsonAsync<JsonElement>();
            var entry = body.GetProperty("topClips").EnumerateArray().Single();
            entry.GetProperty("clip").GetProperty("id").GetGuid().Should().Be(clipId);
            entry.GetProperty("clip").GetProperty("likedByMe").GetBoolean().Should().BeTrue();
        }

        // Anonymous request hits the same cached entry within TTL and must see false.
        using (var anon = _factory!.CreateClient())
        {
            var body = await (await anon.GetAsync("/leaderboards?window=week")).Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("topClips").EnumerateArray()
                .Select(e => e.GetProperty("clip").GetProperty("likedByMe").GetBoolean())
                .Should().OnlyContain(l => l == false);
        }

        // And the original direction: liker hits the cache after anon and still sees their like.
        using (var likerClient = AuthTestHelpers.CreateBearerClient(_factory!, likerToken))
        {
            var body = await (await likerClient.GetAsync("/leaderboards?window=week")).Content.ReadFromJsonAsync<JsonElement>();
            var entry = body.GetProperty("topClips").EnumerateArray().Single();
            entry.GetProperty("clip").GetProperty("id").GetGuid().Should().Be(clipId);
            entry.GetProperty("clip").GetProperty("likedByMe").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task GameLeaderboard_Cached_LikedByMeStaysPerCaller()
    {
        // Same isolation guarantee for the per-game endpoint. Different code path (different
        // handler, different cache key), so test it separately. Cover both warm directions
        // so an authed warm can't leak likedByMe=true into the cache.
        await _fx.ResetAsync();
        var (likerId, likerToken) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "liker");
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var gameId = await GetGameIdBySlugAsync("valorant");
        var (clipId, _) = await SeedClipAsync(authorId, DateTimeOffset.UtcNow, gameId, "game-lb-cached");
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = likerId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) });
            await db.SaveChangesAsync();
        }

        // Authed warms the cache first.
        using (var likerClient = AuthTestHelpers.CreateBearerClient(_factory!, likerToken))
        {
            var body = await (await likerClient.GetAsync("/games/valorant/leaderboard?window=week")).Content.ReadFromJsonAsync<JsonElement>();
            var entry = body.GetProperty("entries").EnumerateArray().Single();
            entry.GetProperty("clip").GetProperty("id").GetGuid().Should().Be(clipId);
            entry.GetProperty("clip").GetProperty("likedByMe").GetBoolean().Should().BeTrue();
        }

        // Anonymous must still see false on the same cached entry.
        using (var anon = _factory!.CreateClient())
        {
            var body = await (await anon.GetAsync("/games/valorant/leaderboard?window=week")).Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("entries").EnumerateArray()
                .Select(e => e.GetProperty("clip").GetProperty("likedByMe").GetBoolean())
                .Should().OnlyContain(l => l == false);
        }

        // And the original direction still holds.
        using (var likerClient = AuthTestHelpers.CreateBearerClient(_factory!, likerToken))
        {
            var body = await (await likerClient.GetAsync("/games/valorant/leaderboard?window=week")).Content.ReadFromJsonAsync<JsonElement>();
            var entry = body.GetProperty("entries").EnumerateArray().Single();
            entry.GetProperty("clip").GetProperty("id").GetGuid().Should().Be(clipId);
            entry.GetProperty("clip").GetProperty("likedByMe").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task GameLeaderboard_LimitClampedAndRanksAssigned()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, "author");
        var gameId = await GetGameIdBySlugAsync("valorant");
        var now = DateTimeOffset.UtcNow;
        var fresh = now.AddHours(-1);

        // Seed 3 clips with decreasing like counts so ranking is unambiguous regardless
        // of tiebreak.
        var clips = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (id, _) = await SeedClipAsync(authorId, now.AddMinutes(-i), gameId, $"c{i}");
            clips.Add(id);
            await SeedLikesAsync(id, fresh, 5 - i);
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/valorant/leaderboard?window=week&limit=2");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entries = body.GetProperty("entries").EnumerateArray().ToList();
        entries.Should().HaveCount(2);
        entries[0].GetProperty("rank").GetInt32().Should().Be(1);
        entries[0].GetProperty("clip").GetProperty("id").GetGuid().Should().Be(clips[0]);
        entries[1].GetProperty("rank").GetInt32().Should().Be(2);
        entries[1].GetProperty("clip").GetProperty("id").GetGuid().Should().Be(clips[1]);
    }

    // ---- helpers ----

    private async Task<int> GetGameIdBySlugAsync(string slug)
    {
        await using var db = _fx.CreateContext();
        return await db.Games.Where(g => g.Slug == slug).Select(g => g.Id).FirstAsync();
    }

    private async Task<(Guid id, string shareCode)> SeedClipAsync(
        Guid userId,
        DateTimeOffset createdAt,
        int? gameId,
        string title,
        string status = "ready",
        string visibility = "public")
    {
        var id = Guid.NewGuid();
        var shareCode = ShareCodeGenerator.Next();
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            GameId = gameId,
            Title = title,
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

    // Each like needs a distinct UserId (composite PK is (UserId, ClipId)), so spin up
    // throwaway user rows per like. Throwaway usernames are unique-per-call to avoid the
    // username UNIQUE constraint across the same test run.
    private async Task SeedLikesAsync(Guid clipId, DateTimeOffset stamp, int count)
    {
        await using var db = _fx.CreateContext();
        for (var i = 0; i < count; i++)
        {
            var userId = Guid.NewGuid();
            db.Users.Add(new User
            {
                Id = userId,
                Username = $"liker-{userId:N}".Substring(0, 20),
                Email = $"{userId:N}@test.local",
            });
            db.Likes.Add(new Like { UserId = userId, ClipId = clipId, CreatedAt = stamp });
        }
        await db.SaveChangesAsync();
    }
}
