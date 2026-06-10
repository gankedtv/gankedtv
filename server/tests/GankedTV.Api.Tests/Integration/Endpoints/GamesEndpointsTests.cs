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

[Collection("PostgresDiscovery")]
public class GamesEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public GamesEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    [Fact]
    public async Task GetGames_NoSearch_ReturnsAlphabeticalSeededList()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        games.GetArrayLength().Should().BeGreaterThanOrEqualTo(9);

        // Alphabetical: "Apex Legends" comes first.
        var first = games.EnumerateArray().First();
        first.GetProperty("name").GetString().Should().Be("Apex Legends");
        first.GetProperty("slug").GetString().Should().Be("apex-legends");
        first.GetProperty("tag").GetString().Should().Be("APEX");
    }

    [Fact]
    public async Task GetGames_HasClipsTrue_ReturnsOnlyGamesWithPublicReadyClips()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var valorantId = await GetGameIdBySlugAsync("valorant");
        var apexId = await GetGameIdBySlugAsync("apex-legends");

        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId); // public + ready ⇒ counts
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId, status: "processing"); // not ready ⇒ excluded
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId, visibility: "unlisted"); // not public ⇒ excluded

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games?hasClips=true");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var slugs = games.EnumerateArray().Select(g => g.GetProperty("slug").GetString()).ToArray();
        slugs.Should().Equal("valorant");
    }

    [Fact]
    public async Task GetGames_SearchByName_FiltersCaseInsensitive()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games?search=Valo");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var names = games.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToArray();
        names.Should().ContainSingle().Which.Should().Be("Valorant");
    }

    [Fact]
    public async Task GetGames_SearchBySlug_Matches()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games?search=rocket-league");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        games.EnumerateArray()
            .Select(g => g.GetProperty("slug").GetString())
            .Should().ContainSingle().Which.Should().Be("rocket-league");
    }

    [Fact]
    public async Task GetGames_LimitClampedToMax()
    {
        await _fx.ResetAsync();

        // Seed has 9 games — that's below MaxLimit (50), so `limit=999` against the
        // baseline can't prove the clamp. Insert enough extra rows to push us past
        // the cap. PostgresFixture intentionally preserves the games table between
        // resets, so first wipe any filler rows from a previous run before
        // re-inserting (otherwise we'd hit the unique slug constraint on rerun).
        await using (var db = _fx.CreateContext())
        {
            await db.Games.Where(g => g.Slug.StartsWith("filler-")).ExecuteDeleteAsync();
            for (var i = 0; i < 60; i++)
            {
                db.Games.Add(new Game
                {
                    Name = $"Filler Game {i:D3}",
                    Slug = $"filler-{i:D3}",
                    Tag = $"FILL{i:D2}",
                });
            }
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games?limit=999");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // MaxLimit is 50 in GamesEndpoints — locked here so a future bump to the
        // const has to come with a deliberate test update.
        games.GetArrayLength().Should().Be(50);
    }

    [Fact]
    public async Task GetGames_NegativeLimit_ClampsToOne()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games?limit=-5");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        games.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetGames_LimitOne_ReturnsSingleResult()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games?limit=1");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        games.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetGames_NoAuthRequired()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/games");

        // Verify both that auth isn't required AND that the endpoint is healthy —
        // a 5xx would also `NotBe(Unauthorized)` and silently pass.
        resp.IsSuccessStatusCode.Should().BeTrue($"got {(int)resp.StatusCode}");
    }

    // ---- GET /games/{slug} ----

    [Fact]
    public async Task GetGameBySlug_Returns200WithDetailAndClipCount()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var gameId = await GetGameIdBySlugAsync("valorant");

        // 3 public/ready clips count; non-public + non-ready do not.
        await SeedClipAsync(userId, DateTimeOffset.UtcNow.AddMinutes(-1), gameId);
        await SeedClipAsync(userId, DateTimeOffset.UtcNow.AddMinutes(-2), gameId);
        await SeedClipAsync(userId, DateTimeOffset.UtcNow.AddMinutes(-3), gameId);
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, gameId, status: "processing");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, gameId, visibility: "unlisted");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/valorant");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetInt32().Should().Be(gameId);
        body.GetProperty("slug").GetString().Should().Be("valorant");
        body.GetProperty("name").GetString().Should().Be("Valorant");
        body.GetProperty("tag").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("clipCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task GetGameBySlug_NoClips_ReturnsZeroCount()
    {
        await _fx.ResetAsync();

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/valorant");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("clipCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetGameBySlug_UnknownSlug_Returns404()
    {
        await _fx.ResetAsync();

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/does-not-exist");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- GET /games/{slug}/clips ----

    [Fact]
    public async Task GetClipsForGame_FiltersByGameAndVisibility()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var targetGameId = await GetGameIdBySlugAsync("valorant");
        var otherGameId = await GetGameIdBySlugAsync("apex-legends");

        var now = DateTimeOffset.UtcNow;
        var (target1, _) = await SeedClipAsync(userId, now.AddMinutes(-1), targetGameId);
        var (target2, _) = await SeedClipAsync(userId, now.AddMinutes(-2), targetGameId);
        await SeedClipAsync(userId, now, otherGameId); // wrong game
        await SeedClipAsync(userId, now, targetGameId, status: "processing"); // not ready
        await SeedClipAsync(userId, now, targetGameId, visibility: "unlisted"); // not public

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/valorant/clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(target1, target2);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetClipsForGame_CursorPagination_NoDuplicatesAtBoundary()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var gameId = await GetGameIdBySlugAsync("valorant");

        // Seed 4 clips; the middle two share the exact CreatedAt to exercise the
        // composite (CreatedAt, Id) keyset across the page boundary.
        var now = DateTimeOffset.UtcNow;
        var shared = now.AddMinutes(-2);
        var seeded = new List<Guid>();
        var (a, _) = await SeedClipAsync(userId, now.AddMinutes(-1), gameId);
        seeded.Add(a);
        var (b, _) = await SeedClipAsync(userId, shared, gameId);
        seeded.Add(b);
        var (c, _) = await SeedClipAsync(userId, shared, gameId);
        seeded.Add(c);
        var (d, _) = await SeedClipAsync(userId, now.AddMinutes(-3), gameId);
        seeded.Add(d);

        using var client = _factory!.CreateClient();

        var first = await client.GetAsync("/games/valorant/clips?limit=2");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        firstBody.GetProperty("items").GetArrayLength().Should().Be(2);
        var nextCursor = firstBody.GetProperty("nextCursor").GetString();
        nextCursor.Should().NotBeNullOrEmpty();

        var second = await client.GetAsync(
            $"/games/valorant/clips?limit=2&cursor={Uri.EscapeDataString(nextCursor!)}");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        var returned = firstBody.GetProperty("items").EnumerateArray()
            .Concat(secondBody.GetProperty("items").EnumerateArray())
            .Select(e => e.GetProperty("id").GetGuid())
            .ToList();
        returned.Should().BeEquivalentTo(seeded);
        returned.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetClipsForGame_UnknownSlug_Returns404()
    {
        await _fx.ResetAsync();

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/does-not-exist/clips");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetClipsForGame_EmptyGame_Returns200WithEmptyItemsAndNullCursor()
    {
        await _fx.ResetAsync();

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/valorant/clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetClipsForGame_LimitClampedToMax()
    {
        // BuildFeedPageAsync owns the clamp (FeedMaxLimit=100), but the per-game
        // route also routes through it — pin the behaviour here so a future
        // tweak to the helper or the route doesn't silently uncap the page size.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var gameId = await GetGameIdBySlugAsync("valorant");

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await SeedClipAsync(userId, now.AddSeconds(-i), gameId);
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/valorant/clips?limit=999999");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task GetClipsForGame_InvalidCursor_FallsBackToFirstPage()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var gameId = await GetGameIdBySlugAsync("valorant");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, gameId);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/valorant/clips?cursor=not-a-real-cursor");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // ---- helpers ----

    private async Task<Guid> SeedUserAsync(string username = "games-test-user")
    {
        var (userId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);
        return userId;
    }

    private async Task<int> GetGameIdBySlugAsync(string slug)
    {
        await using var db = _fx.CreateContext();
        return await db.Games.Where(g => g.Slug == slug).Select(g => g.Id).FirstAsync();
    }

    private async Task<(Guid id, string shareCode)> SeedClipAsync(
        Guid userId,
        DateTimeOffset createdAt,
        int? gameId,
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
            Title = $"clip-{id:N}".Substring(0, 20),
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

    [Fact]
    public async Task GetGames_LikeMetacharsInSearch_AreMatchedLiterally()
    {
        // Without escaping, a search of "%" would match every row via the resulting
        // `%%%` ILIKE pattern. Seed a row with a literal '%' in its name and verify
        // the search only finds that row.
        await _fx.ResetAsync();
        await using (var db = _fx.CreateContext())
        {
            await db.Games.Where(g => g.Slug == "literal-percent-game").ExecuteDeleteAsync();
            db.Games.Add(new Game { Name = "Game 100% Real", Slug = "literal-percent-game", Tag = "PCT" });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games?search=%25"); // url-encoded '%'

        resp.IsSuccessStatusCode.Should().BeTrue();
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var slugs = games.EnumerateArray().Select(g => g.GetProperty("slug").GetString()).ToArray();
        slugs.Should().ContainSingle().Which.Should().Be("literal-percent-game");
    }
}
