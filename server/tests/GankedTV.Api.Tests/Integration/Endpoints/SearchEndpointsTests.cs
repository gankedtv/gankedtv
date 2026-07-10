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
    public async Task Search_PunctuatedQuery_SplitsTokensAtSeparators()
    {
        // Regression: the previous tokenizer stripped non-alphanumeric chars *within*
        // a whitespace-split token, so "Counter-Strike" became one fused lexeme
        // "CounterStrike:*" that didn't match any of the actual stored lexemes
        // ("counter", "strike", "counter-strike"). The regex-based tokenizer splits
        // on punctuation instead, producing "counter:* & strike:*" — both lexemes
        // exist in the tsvector, so the game surfaces.
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search?q=Counter-Strike&type=games");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var slugs = body.GetProperty("games").EnumerateArray()
            .Select(g => g.GetProperty("slug").GetString()).ToArray();
        slugs.Should().Contain("cs2");
    }

    [Fact]
    public async Task Search_ShortQuery_MatchesTokenPrefix()
    {
        // 2-char "va" becomes the tsquery lexeme `va:*`, which matches any token that
        // *starts* with "va" (anywhere in the title, not just title-prefix). It must
        // still reject titles that only contain "va" mid-word: "Save" tokenizes as
        // "save", and "save" doesn't start with "va".
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var apexId = await GetGameIdBySlugAsync("apex-legends");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId, title: "Vault chase");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId, title: "Save the round"); // "save" doesn't start with "va"

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
    public async Task Search_NumericToken_MatchesTokenAnywhereInTitle()
    {
        // Regression: with the old plainto_tsquery + 3-char-cutoff design, searching
        // "04" fell into the ILIKE-prefix branch and only matched titles *starting*
        // with "04" — so "Seed Clip 04" never surfaced. With to_tsquery(`04:*`) the
        // numeric token matches wherever it appears in the tsvector.
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var apexId = await GetGameIdBySlugAsync("apex-legends");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId, title: "Seed Clip 04");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, apexId, title: "Seed Clip 07");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=04&type=clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var titles = body.GetProperty("clips").EnumerateArray()
            .Select(c => c.GetProperty("title").GetString()).ToArray();
        titles.Should().ContainSingle().Which.Should().Be("Seed Clip 04");
    }

    [Fact]
    public async Task Search_HidesUnlistedPrivateAndProcessingClips()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var valorantId = await GetGameIdBySlugAsync("valorant");

        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Valorant public", status: "ready", visibility: "public");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Valorant unlisted", visibility: "unlisted");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Valorant private", visibility: "private");
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
    public async Task Search_MatchesUsers_PrefixOutranksSubstring()
    {
        await _fx.ResetAsync();
        await SeedUserAsync("thegankster");
        await SeedUserAsync("gank-lord");
        await SeedUserAsync("unrelated");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=gank");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var usernames = body.GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("username").GetString()).ToList();
        // Prefix match first, substring match second, non-match absent.
        usernames.Should().Equal("gank-lord", "thegankster");
    }

    [Fact]
    public async Task Search_TypeUsers_ReturnsOnlyUsers()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync("valorantfan");
        var valorantId = await GetGameIdBySlugAsync("valorant");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, valorantId, title: "Valorant clip");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=valorant&type=users");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("clips").GetArrayLength().Should().Be(0);
        body.GetProperty("games").GetArrayLength().Should().Be(0);
        var usernames = body.GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("username").GetString()).ToList();
        usernames.Should().Equal("valorantfan");
    }

    [Fact]
    public async Task Search_BannedUser_NeverReturned()
    {
        await _fx.ResetAsync();
        var bannedId = await SeedUserAsync("gankedvillain");
        await using (var db = _fx.CreateContext())
        {
            await db.Users.Where(u => u.Id == bannedId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.BannedAt, DateTimeOffset.UtcNow)
                    .SetProperty(u => u.BannedReason, "test"));
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=gankedvillain&type=users");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("users").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Search_TokenlessQuery_StillMatchesUsersByLiteralCharacters()
    {
        // "_" tokenizes to nothing (tsQuery null → clips/games short-circuit to empty),
        // but the users leg matches on the literal character, so underscore-heavy
        // usernames stay findable.
        await _fx.ResetAsync();
        await SeedUserAsync("cool_user");
        await SeedUserAsync("plainname");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/search?q=_");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("clips").GetArrayLength().Should().Be(0);
        body.GetProperty("games").GetArrayLength().Should().Be(0);
        var usernames = body.GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("username").GetString()).ToList();
        usernames.Should().Equal("cool_user");
    }

    [Fact]
    public async Task Search_TsqueryMetacharacters_DoNotCause500()
    {
        // BuildPrefixTsQuery's allowlist sanitization strips tsquery operators (!, &,
        // |, :, *) before they reach to_tsquery. Without that guard, to_tsquery would
        // 500 on a malformed expression like "!&|".
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

    [Fact]
    public async Task Search_AllNonAlphanumericQuery_Returns200WithEmptyResults()
    {
        // Replaces the old "LIKE escape" theory. Sanitization now strips anything
        // that isn't a letter/digit before building the tsquery; a query that's
        // purely punctuation tokenizes to nothing, so the response is a 200 with
        // empty halves rather than a 500 from a malformed to_tsquery expression.
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/search?q=%5C%5F%26%21"); // "\_&!"

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("clips").GetArrayLength().Should().Be(0);
        body.GetProperty("games").GetArrayLength().Should().Be(0);
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
