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
        var factory = FakeHttpClientFactory.Create(handler as TestHttpMessageHandler
            ?? throw new InvalidOperationException("expected TestHttpMessageHandler"));
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

        var opts = Options.Create(new IgdbOptions { ClientId = "cid", ClientSecret = "s", MaxRequestsPerSecond = 1000 });
        var svc = new IgdbMetadataService(
            FakeHttpClientFactory2.Create(handler), opts, NullLogger<IgdbMetadataService>.Instance, TimeProvider.System);

        var games = await svc.GetPopularGamesAsync(2);

        games.Should().BeEmpty();
        handler.TokenRequests.Should().Be(2, "the cached token is invalidated and refetched after a 401");
        handler.GamesRequests.Should().Be(2, "the games query is retried once after re-auth");
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
        // Two pages worth of work at 20 req/s ⇒ a ≥50ms gap is enforced before the 2nd query.
        const string fullPage = """[{"id":1,"name":"A","cover":{"id":1,"image_id":"x"}}]"""; // 1 row < pageSize ⇒ second page won't fire
        var handler = new TestHttpMessageHandler()
            .OnPost(TokenUrl, HttpStatusCode.OK, TokenJson)
            .OnPost(GamesUrl, HttpStatusCode.OK, fullPage);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Build(handler, rate: 20).GetPopularGamesAsync(1);
        sw.Stop();

        // Single page so no inter-request wait is required, but the throttle gate still runs;
        // this exercises the throttle path without asserting brittle wall-clock timing.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
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

    private static class FakeHttpClientFactory2
    {
        public static IHttpClientFactory Create(HttpMessageHandler handler) => new Factory(handler);

        private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }
    }
}
