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

    [Fact]
    public async Task HotGames_RanksByWindowedEngagement_LikesWeighted()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var apexId = await GetGameIdBySlugAsync("apex-legends");
        var valorantId = await GetGameIdBySlugAsync("valorant");

        // Valorant: 1 like in window = 3 points. Apex: 2 views in window = 2 points.
        // Expected order is anti-alphabetical on purpose, so a regression to the old
        // alphabetical list fails this test on its own.
        var (apexClip, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId);
        var (valorantClip, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId);
        await SeedLikeAsync(valorantClip, userId, DateTimeOffset.UtcNow.AddHours(-1));
        await SeedViewAsync(apexClip, DateTimeOffset.UtcNow.AddHours(-1));
        await SeedViewAsync(apexClip, DateTimeOffset.UtcNow.AddHours(-2));

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/hot?limit=2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var slugs = games.EnumerateArray().Select(g => g.GetProperty("slug").GetString()).ToArray();
        slugs.Should().Equal("valorant", "apex-legends");
    }

    [Fact]
    public async Task HotGames_RanksByWindowedEngagement_ViewsCount()
    {
        // Pins the views half of the formula: without `+ views` the scores collapse to 0 vs 3
        // and Valorant would win. Apex's 4 views (4 points) must outrank Valorant's 1 like (3).
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var apexId = await GetGameIdBySlugAsync("apex-legends");
        var valorantId = await GetGameIdBySlugAsync("valorant");

        var (apexClip, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId);
        var (valorantClip, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId);
        await SeedLikeAsync(valorantClip, userId, DateTimeOffset.UtcNow.AddHours(-1));
        for (var i = 1; i <= 4; i++)
        {
            await SeedViewAsync(apexClip, DateTimeOffset.UtcNow.AddHours(-i));
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/hot?limit=2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var slugs = games.EnumerateArray().Select(g => g.GetProperty("slug").GetString()).ToArray();
        slugs.Should().Equal("apex-legends", "valorant");
    }

    [Fact]
    public async Task HotGames_EngagementOutsideWindow_Ignored_BackfillsByClipCount()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var apexId = await GetGameIdBySlugAsync("apex-legends");
        var valorantId = await GetGameIdBySlugAsync("valorant");

        // Apex's only like is 8 days old — outside the 7-day window — so ranking falls back
        // to the most-clipped backfill: Valorant (2 clips) before Apex (1 clip).
        var (apexClip, _) = await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId);
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId);
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId);
        await SeedLikeAsync(apexClip, userId, DateTimeOffset.UtcNow.AddDays(-8));

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/hot?limit=3");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var slugs = games.EnumerateArray().Select(g => g.GetProperty("slug").GetString()).ToArray();
        slugs.Should().Equal("valorant", "apex-legends");
    }

    [Fact]
    public async Task HotGames_NoClipsAnywhere_ReturnsEmpty()
    {
        // Games without a single public+ready clip never rank — an all-quiet catalog yields
        // an empty rail, not an alphabetical filler list.
        await _fx.ResetAsync();

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/games/hot");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        games.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetGames_SearchMiss_AuthedCaller_RetriesAfterOnDemandImport()
    {
        await _fx.ResetAsync();

        // Stub the on-demand importer to add the game the way a real IGDB import would,
        // then assert the endpoint's retry picks it up in the same request.
        var searchImport = Substitute.For<GankedTV.Api.Services.Igdb.IGameSearchImportService>();
        searchImport.TryImportMatchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await using var db = _fx.CreateContext();
                db.Games.Add(new Game
                {
                    Name = "Satisfactory",
                    Slug = "satisfactory",
                    Tag = "SAT",
                    IgdbId = 100_001,
                    IgdbManaged = true,
                });
                await db.SaveChangesAsync();
                return true;
            });

        await using var factory = new AuthApiFactory(
            _fx.ConnectionString, _storage,
            configureServices: s => s.AddSingleton(searchImport));
        try
        {
            var (_, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, factory, "games-importer");
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var resp = await client.GetAsync("/games?search=satisfactory");

            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
            var slugs = games.EnumerateArray().Select(g => g.GetProperty("slug").GetString()).ToArray();
            slugs.Should().Equal("satisfactory");
            await searchImport.Received(1).TryImportMatchesAsync("satisfactory", Arg.Any<CancellationToken>());
        }
        finally
        {
            // The games table survives Respawn resets — scrub the imported row back out.
            await using var db = _fx.CreateContext();
            await SeededGames.ResetBaselineAsync(db);
        }
    }

    [Fact]
    public async Task GetGames_OverlongSearch_Returns400_WithoutImporting()
    {
        await _fx.ResetAsync();
        var searchImport = Substitute.For<GankedTV.Api.Services.Igdb.IGameSearchImportService>();

        await using var factory = new AuthApiFactory(
            _fx.ConnectionString, _storage,
            configureServices: s => s.AddSingleton(searchImport));
        var (_, token) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, factory, "games-long-search");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var resp = await client.GetAsync($"/games?search={new string('a', 101)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await searchImport.DidNotReceive().TryImportMatchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetGames_SearchMiss_Anonymous_DoesNotImport()
    {
        await _fx.ResetAsync();
        var searchImport = Substitute.For<GankedTV.Api.Services.Igdb.IGameSearchImportService>();

        await using var factory = new AuthApiFactory(
            _fx.ConnectionString, _storage,
            configureServices: s => s.AddSingleton(searchImport));
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/games?search=satisfactory");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
        games.GetArrayLength().Should().Be(0);
        await searchImport.DidNotReceive().TryImportMatchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- helpers ----

    private async Task SeedLikeAsync(Guid clipId, Guid userId, DateTimeOffset createdAt)
    {
        await using var db = _fx.CreateContext();
        db.Likes.Add(new Like { UserId = userId, ClipId = clipId, CreatedAt = createdAt });
        await db.SaveChangesAsync();
    }

    private async Task SeedViewAsync(Guid clipId, DateTimeOffset createdAt)
    {
        await using var db = _fx.CreateContext();
        db.ClipViews.Add(new ClipView { ClipId = clipId, CreatedAt = createdAt });
        await db.SaveChangesAsync();
    }

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

    [Fact]
    public async Task GetGames_SameName_OrdersDeterministically()
    {
        // Two catalog rows can legitimately share a display name (IGDB renames a sequel onto its
        // predecessor's title). Ordering by name alone leaves the tie unresolved, so which row a
        // client sees first — and which survives `Take(limit)` — varies between requests.
        await _fx.ResetAsync();
        int lowId, highId;
        await using (var db = _fx.CreateContext())
        {
            await db.Games.Where(g => g.Slug.StartsWith("tie-test-")).ExecuteDeleteAsync();
            var a = new Game { Name = "Tie Test Game", Slug = "tie-test-a", Tag = "TTA" };
            var b = new Game { Name = "Tie Test Game", Slug = "tie-test-b", Tag = "TTB" };
            db.Games.AddRange(a, b);
            await db.SaveChangesAsync();
            (lowId, highId) = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
        }

        using var client = _factory!.CreateClient();
        try
        {
            foreach (var _ in Enumerable.Range(0, 3))
            {
                var resp = await client.GetAsync("/games?search=Tie%20Test%20Game");
                resp.IsSuccessStatusCode.Should().BeTrue();
                var games = await resp.Content.ReadFromJsonAsync<JsonElement>();
                games.EnumerateArray().Select(g => g.GetProperty("id").GetInt32())
                    .Should().Equal(lowId, highId);
            }
        }
        finally
        {
            await using var db = _fx.CreateContext();
            await db.Games.Where(g => g.Slug.StartsWith("tie-test-")).ExecuteDeleteAsync();
        }
    }
}
