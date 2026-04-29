using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
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
