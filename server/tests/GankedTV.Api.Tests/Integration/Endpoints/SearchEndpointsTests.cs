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
public class SearchEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public SearchEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _storage = Substitute.For<IObjectStorageService>();
        // The clip half of the response signs thumbnail URLs via ProjectFeedItemsAsync,
        // which calls GetPresignedGetUrl. NSubstitute's default returns "" for strings —
        // that's enough for ClipFeedItem.ThumbnailUrl to be a non-null string and is
        // sufficient for everything these tests assert.
        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns("https://thumb.test/x.jpg");
        _factory = new AuthApiFactory(_fx.ConnectionString, _storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Search_EmptyQuery_Returns400()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search?q=");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_query");
    }

    [Fact]
    public async Task Search_WhitespaceQuery_Returns400()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search?q=%20%20%20");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_MissingQuery_Returns400()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_LongQuery_ReturnsClipsAndGamesMatches()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var valorantId = await GetGameIdBySlugAsync("valorant");

        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Insane Valorant Ace");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "League clutch"); // shouldn't match

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=valorant");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var clipTitles = body.GetProperty("clips").EnumerateArray()
            .Select(c => c.GetProperty("title").GetString()).ToArray();
        clipTitles.Should().ContainSingle().Which.Should().Be("Insane Valorant Ace");

        var gameSlugs = body.GetProperty("games").EnumerateArray()
            .Select(g => g.GetProperty("slug").GetString()).ToArray();
        gameSlugs.Should().Contain("valorant");
    }

    [Fact]
    public async Task Search_ShortQuery_UsesPrefixFallback()
    {
        // 2-char "va" wouldn't tokenize into a useful tsquery lexeme, so the endpoint
        // falls back to ILIKE 'va%'. Prefix-only — "va" must not match games like
        // "Counter-Strike 2" or clip titles that merely contain "va" mid-word.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var apexId = await GetGameIdBySlugAsync("apex-legends");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId, title: "Vault chase");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId, title: "Save the round"); // contains "va" mid-word, must NOT match

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=va");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var gameNames = body.GetProperty("games").EnumerateArray()
            .Select(g => g.GetProperty("name").GetString()).ToArray();
        gameNames.Should().ContainSingle().Which.Should().Be("Valorant");

        var clipTitles = body.GetProperty("clips").EnumerateArray()
            .Select(c => c.GetProperty("title").GetString()).ToArray();
        clipTitles.Should().ContainSingle().Which.Should().Be("Vault chase");
    }

    [Fact]
    public async Task Search_HidesUnlistedAndProcessingClips()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var valorantId = await GetGameIdBySlugAsync("valorant");

        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Valorant public", status: "ready", visibility: "public");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Valorant unlisted", visibility: "unlisted");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Valorant processing", status: "processing");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=valorant&type=clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var titles = body.GetProperty("clips").EnumerateArray()
            .Select(c => c.GetProperty("title").GetString()).ToArray();
        titles.Should().ContainSingle().Which.Should().Be("Valorant public");
    }

    [Fact]
    public async Task Search_TypeClips_ReturnsEmptyGames()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search?q=valorant&type=clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("games").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Search_TypeGames_ReturnsEmptyClips()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var valorantId = await GetGameIdBySlugAsync("valorant");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Valorant clip");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=valorant&type=games");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("clips").GetArrayLength().Should().Be(0);
        body.GetProperty("games").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Search_TsqueryMetacharacters_DoNotCause500()
    {
        // PlainToTsQuery treats input as a plain phrase, so operators like !, &, |, : *
        // never reach the tsquery parser. Without that guard, raw to_tsquery would 500 on
        // a malformed query like "!&|".
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search?q=%21%26%7C%3A%2A"); // "!&|:*"

        resp.IsSuccessStatusCode.Should().BeTrue($"got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Search_LimitClampedToMax()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var valorantId = await GetGameIdBySlugAsync("valorant");
        for (var i = 0; i < 60; i++)
        {
            await SeedClipAsync(userId, DateTimeOffset.UtcNow.AddSeconds(-i), valorantId,
                title: $"Valorant clutch {i:D3}");
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=valorant&type=clips&limit=999");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // MaxLimit is 50 in SearchEndpoints — pinned so a future bump comes with a deliberate update.
        body.GetProperty("clips").GetArrayLength().Should().Be(50);
    }

    [Fact]
    public async Task Search_UnknownType_Returns400()
    {
        // A typo like `type=clip` (singular) used to silently return empty halves; now
        // it 400s with code=invalid_type so the misuse surfaces immediately.
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search?q=valorant&type=clip");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_type");
    }

    [Fact]
    public async Task Search_TitleMatch_OutranksDescriptionMatch()
    {
        // The weighted tsvector (title=A, description=B) is the whole reason we use
        // setweight in the migration. If a future change drops the weights, this test
        // pins the behaviour: a clip with "valorant" in the title must rank above a
        // clip that only mentions it in the description.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var apexId = await GetGameIdBySlugAsync("apex-legends");

        var (descOnlyId, _) = await SeedClipAsync(
            userId,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            apexId,
            title: "Random clutch",
            description: "valorant comparison footage");
        var (titleMatchId, _) = await SeedClipAsync(
            userId,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            apexId,
            title: "valorant ace",
            description: "no body text");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=valorant&type=clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("clips").EnumerateArray()
            .Select(c => c.GetProperty("id").GetGuid()).ToList();
        // Title-match must come first despite being older (CreatedAt only breaks ties).
        ids.Should().HaveCount(2);
        ids[0].Should().Be(titleMatchId);
        ids[1].Should().Be(descOnlyId);
    }

    [Theory]
    [InlineData("%5C", "snake")] // url-encoded backslash
    [InlineData("%5F", "snake")] // url-encoded underscore
    public async Task Search_PrefixFallback_EscapesLikeMetacharacters(string encodedChar, string seedPrefix)
    {
        // Without escaping, a 2-char query like `\` or `_` would interpret as wildcard
        // and match every row. Each variant seeds a literal-char clip + a sibling and
        // asserts the search resolves to a single clip whose title actually starts
        // with the metacharacter.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var apexId = await GetGameIdBySlugAsync("apex-legends");

        var rawChar = Uri.UnescapeDataString(encodedChar);
        var matchTitle = rawChar + seedPrefix; // e.g. "_snake" or "\snake"
        var (matchId, _) = await SeedClipAsync(
            userId, DateTimeOffset.UtcNow, apexId, title: matchTitle);
        await SeedClipAsync(
            userId, DateTimeOffset.UtcNow, apexId, title: "other clip");

        using var client = _factory!.CreateClient();
        // Two-char query exercises the prefix-fallback branch (< FullTextMinLength=3).
        var resp = await client.GetAsync($"/search?q={encodedChar}s&type=clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("clips").EnumerateArray()
            .Select(c => c.GetProperty("id").GetGuid()).ToList();
        ids.Should().ContainSingle().Which.Should().Be(matchId);
    }

    [Fact]
    public async Task Search_NoAuthRequired()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search?q=valorant");

        resp.IsSuccessStatusCode.Should().BeTrue($"got {(int)resp.StatusCode}");
    }

    // ---- helpers ----

    private async Task<Guid> SeedUserAsync(string username = "search-test-user")
    {
        var (userId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);
        return userId;
    }

    private async Task<int> GetGameIdBySlugAsync(string slug)
    {
        await using var db = _fx.CreateContext();
        return await db.Games.Where(g => g.Slug == slug).Select(g => g.Id).FirstAsync();
    }

    // Returns (id, _) so call sites can `var (id, _)` for readability — matches the shape
    // GamesEndpointsTests uses for its seeder.
    private async Task<(Guid id, string shareCode)> SeedClipAsync(
        Guid userId,
        DateTimeOffset createdAt,
        int? gameId,
        string title = "test clip",
        string status = "ready",
        string visibility = "public",
        string? description = null)
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
            Description = description,
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
}
