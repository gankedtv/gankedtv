using System.Linq;
using System.Net;
using FluentAssertions;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Services.Igdb;

public class IgdbMetadataServiceTests
{
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string GamesUrl = "https://api.igdb.com/v4/games";
    private const string ImageUrl = "https://images.igdb.com";

    private const string TokenJson = """{"access_token":"tok-123","expires_in":3600,"token_type":"bearer"}""";

    private static IgdbMetadataService Build(HttpMessageHandler handler, int rate = 1000)
    {
        var opts = Options.Create(new IgdbOptions
        {
            ClientId = "cid",
            ClientSecret = "secret",
            MaxRequestsPerSecond = rate,
        });
        var factory = FakeHttpClientFactory.Create(handler);
        return new IgdbMetadataService(factory, opts, NullLogger<IgdbMetadataService>.Instance, TimeProvider.System);
    }

    [Fact]
    public async Task GetPopularGamesAsync_ReturnsGamesWithCovers_SkippingThoseWithout()
    {
        const string gamesJson = """
        [
          {"id":1,"name":"Game One","cover":{"id":10,"image_id":"img1"}},
          {"id":2,"name":"Game Two","cover":{"id":11,"image_id":"img2"}},
          {"id":3,"name":"No Cover","cover":null}
        ]
        """;
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, gamesJson);

        var games = await Build(handler).GetPopularGamesAsync(2);

        games.Should().HaveCount(2);
        games[0].Should().Be(new IgdbGame(1, "Game One", "img1"));
        games[1].Should().Be(new IgdbGame(2, "Game Two", "img2"));
    }

    [Fact]
    public async Task GetPopularGamesAsync_PagesByMaxSize_AndCapsResultsToCount()
    {
        // Regression: the query must request a full page (limit 500) regardless of how many
        // rows are still wanted, so filtered cover-less rows can't make the offset overshoot
        // and skip games. Results are still capped to the requested count.
        const string gamesJson = """
        [
          {"id":1,"name":"A","cover":{"id":1,"image_id":"a"}},
          {"id":2,"name":"B","cover":{"id":2,"image_id":"b"}},
          {"id":3,"name":"C","cover":{"id":3,"image_id":"c"}}
        ]
        """;
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, gamesJson);

        var games = await Build(handler).GetPopularGamesAsync(2);

        games.Should().HaveCount(2, "results are capped to the requested count");
        var gamesQuery = handler.CapturedBodies.Single(b => b.Body.Contains("fields name", StringComparison.Ordinal));
        gamesQuery.Body.Should().Contain("limit 500");
    }

    [Fact]
    public async Task GetPopularGamesAsync_RequestsAndParsesAlternativeNames()
    {
        // The importer reconciles renamed games through IGDB's alias list, so the query has to
        // ask for it and the parse has to survive rows that have none.
        const string gamesJson = """
        [
          {"id":125174,"name":"Overwatch","cover":{"id":10,"image_id":"img1"},
           "alternative_names":[{"name":"Overwatch 2"},{"name":"OW2"},{"name":""}]},
          {"id":2,"name":"No Aliases","cover":{"id":11,"image_id":"img2"}}
        ]
        """;
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, gamesJson);

        var games = await Build(handler).GetPopularGamesAsync(2);

        handler.CapturedBodies.Single(b => b.Body.Contains("fields name", StringComparison.Ordinal))
            .Body.Should().Contain("alternative_names.name");
        games[0].AlternativeNames.Should().Equal("Overwatch 2", "OW2");
        games[1].AlternativeNames.Should().BeNull("no aliases stays null, not an empty list");
    }

    [Fact]
    public async Task SearchGamesAsync_RequestsAlternativeNames()
    {
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, "[]");

        await Build(handler).SearchGamesAsync("overwatch", 5);

        handler.CapturedBodies.Single(b => b.Body.Contains("search ", StringComparison.Ordinal))
            .Body.Should().Contain("alternative_names.name");
    }

    [Fact]
    public async Task GetPopularGamesAsync_SendsClientIdAndBearerToken()
    {
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, "[]");

        await Build(handler).GetPopularGamesAsync(2);

        var gamesReq = handler.Requests.Single(r => r.RequestUri!.ToString().StartsWith(GamesUrl, StringComparison.Ordinal));
        gamesReq.Headers.GetValues("Client-ID").Should().ContainSingle().Which.Should().Be("cid");
        gamesReq.Headers.Authorization!.Scheme.Should().Be("Bearer");
        gamesReq.Headers.Authorization.Parameter.Should().Be("tok-123");
    }

    [Fact]
    public async Task GetPopularGamesAsync_CachesTokenAcrossCalls()
    {
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, "[]");
        var svc = Build(handler);

        await svc.GetPopularGamesAsync(2);
        await svc.GetPopularGamesAsync(2);

        handler.Requests.Count(r => r.RequestUri!.ToString().StartsWith(TokenUrl, StringComparison.Ordinal))
            .Should().Be(1, "the token is cached until near expiry");
    }

    [Fact]
    public async Task GetPopularGamesAsync_Retries401ByReauthenticating()
    {
        var handler = new SequencedGamesHandler(TokenJson, "[]");

        var svc = Build(handler);

        var games = await svc.GetPopularGamesAsync(2);

        games.Should().BeEmpty();
        handler.TokenRequests.Should().Be(2, "the cached token is invalidated and refetched after a 401");
        handler.GamesRequests.Should().Be(2, "the games query is retried once after re-auth");
    }

    [Fact]
    public async Task SearchGamesAsync_SendsSearchQuery_AndFiltersCoverlessRows()
    {
        const string gamesJson = """
        [
          {"id":100,"name":"Satisfactory","cover":{"id":10,"image_id":"sat1"}},
          {"id":101,"name":"Satisfactorio","cover":null}
        ]
        """;
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, gamesJson);

        var games = await Build(handler).SearchGamesAsync("satisfactory", 5);

        games.Should().ContainSingle().Which.Should().Be(new IgdbGame(100, "Satisfactory", "sat1"));
        var query = handler.CapturedBodies.Single(b => b.Body.Contains("search", StringComparison.Ordinal)).Body;
        query.Should().Contain("search \"satisfactory\";");
        query.Should().Contain("where cover != null & game_type = 0 & version_parent = null;");
        query.Should().Contain("limit 5;");
        query.Should().NotContain("sort", "IGDB rejects sort combined with search");
    }

    [Fact]
    public async Task SearchGamesAsync_EscapesQuotesAndBackslashes()
    {
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, "[]");

        await Build(handler).SearchGamesAsync("say \"hi\" \\ bye", 3);

        var query = handler.CapturedBodies.Single(b => b.Body.Contains("search", StringComparison.Ordinal)).Body;
        query.Should().Contain("search \"say \\\"hi\\\" \\\\ bye\";");
    }

    [Fact]
    public async Task DownloadCoverAsync_ReturnsBytes()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(ImageUrl, HttpStatusCode.OK, "JPEGBYTES");

        var bytes = await Build(handler).DownloadCoverAsync("img1");

        bytes.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(bytes!).Should().Be("JPEGBYTES");
    }

    [Fact]
    public async Task DownloadCoverAsync_RequestsConfiguredSizeAndImageId()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(ImageUrl, HttpStatusCode.OK, "JPEGBYTES");

        await Build(handler).DownloadCoverAsync("img42");

        var req = handler.Requests.Single();
        req.RequestUri!.ToString().Should().Be(
            "https://images.igdb.com/igdb/image/upload/t_cover_big_2x/img42.jpg");
    }

    [Fact]
    public async Task DownloadCoverAsync_ReturnsNullOn404()
    {
        var handler = new TestHttpMessageHandler()
            .OnGet(ImageUrl, HttpStatusCode.NotFound, "missing");

        var bytes = await Build(handler).DownloadCoverAsync("nope");

        bytes.Should().BeNull();
    }

    [Fact]
    public async Task GetPopularGamesAsync_ThrottlesBetweenRequests()
    {
        // A full page (== MaxPageSize 500 rows) forces a second /games request; at 20 req/s the
        // throttle must space the two requests by ≥50ms.
        var rows = string.Join(",", Enumerable.Range(1, 500)
            .Select(i => $"{{\"id\":{i},\"name\":\"G{i}\",\"cover\":{{\"id\":{i},\"image_id\":\"x{i}\"}}}}"));
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, $"[{rows}]");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Build(handler, rate: 20).GetPopularGamesAsync(600);
        sw.Stop();

        handler.Requests.Count(r => r.RequestUri!.ToString().StartsWith(GamesUrl, StringComparison.Ordinal))
            .Should().Be(2, "600 wanted > 500 per page ⇒ a second page is fetched");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async Task GetPopularGamesAsync_SkipsGamesWithEmptyNameOrImageId()
    {
        const string gamesJson = """
        [
          {"id":1,"name":"","cover":{"id":1,"image_id":"a"}},
          {"id":2,"name":"Valid","cover":{"id":2,"image_id":""}},
          {"id":3,"name":"Good","cover":{"id":3,"image_id":"img3"}}
        ]
        """;
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, gamesJson);

        var games = await Build(handler).GetPopularGamesAsync(10);

        // Empty name and empty image_id are both rejected; only the fully-populated row survives.
        games.Should().ContainSingle().Which.Should().Be(new IgdbGame(3, "Good", "img3"));
    }

    [Fact]
    public async Task GetPopularGamesAsync_NullGamesResponse_ReturnsEmpty()
    {
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, "null");

        (await Build(handler).GetPopularGamesAsync(2)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetPopularGamesAsync_NullTokenResponse_Throws()
    {
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, "null")
            .OnPost(GamesUrl, HttpStatusCode.OK, "[]");

        var act = async () => await Build(handler).GetPopularGamesAsync(2);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty*");
    }

    [Fact]
    public async Task GetPopularGamesAsync_TokenWithoutAccessToken_Throws()
    {
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, """{"expires_in":3600,"token_type":"bearer"}""")
            .OnPost(GamesUrl, HttpStatusCode.OK, "[]");

        var act = async () => await Build(handler).GetPopularGamesAsync(2);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*access_token*");
    }

    // Stateful handler for the 401-retry path: first /games call → 401, subsequent → 200.
    // Token endpoint always 200; counts both so the test can assert a re-auth happened.
    private sealed class SequencedGamesHandler(string tokenJson, string gamesJson) : HttpMessageHandler
    {
        public int TokenRequests { get; private set; }
        public int GamesRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith(TokenUrl, StringComparison.Ordinal))
            {
                TokenRequests++;
                return Task.FromResult(Json(HttpStatusCode.OK, tokenJson));
            }

            GamesRequests++;
            var status = GamesRequests == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            return Task.FromResult(Json(status, GamesRequests == 1 ? "unauthorized" : gamesJson));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
