using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresClips")]
public class SharePreviewEndpointsTests : IAsyncLifetime
{
    private const string WebOrigin = "http://localhost:5173";

    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public SharePreviewEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _storage = Substitute.For<IObjectStorageService>();
        _storage.GetPresignedGetUrl(
                Arg.Any<string>(), Arg.Is<string>(k => k.StartsWith("thumbs/")), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/thumb.jpg");
        _storage.GetPresignedGetUrl(
                Arg.Any<string>(), Arg.Is<string>(k => k.EndsWith(".mp4")), Arg.Any<TimeSpan?>())
            .Returns("https://cdn.example.com/video.mp4");
        _factory = new AuthApiFactory(_fx.ConnectionString, _storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "sharer") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private async Task<(Guid id, string shareCode)> SeedClipAsync(
        Guid userId,
        string status = "ready",
        string visibility = "public",
        string? title = null)
    {
        var id = Guid.NewGuid();
        var shareCode = ShareCodeGenerator.Next();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
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
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (id, shareCode);
    }

    private HttpClient NoRedirectClient() =>
        _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // ---- GET /c/{code}/poster.jpg ----

    [Fact]
    public async Task Poster_Anonymous_RedirectsToPresignedThumbnail()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId);

        using var client = NoRedirectClient();
        var resp = await client.GetAsync($"/c/{shareCode}/poster.jpg");

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.ToString().Should().Be("https://cdn.example.com/thumb.jpg");
    }

    [Fact]
    public async Task Poster_PrivateClip_AnonymousReturns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, visibility: "private");

        using var client = NoRedirectClient();
        var resp = await client.GetAsync($"/c/{shareCode}/poster.jpg");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- GET /c/{code}/video.mp4 ----

    [Fact]
    public async Task Video_Anonymous_RedirectsToPresignedMaster()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId);

        using var client = NoRedirectClient();
        var resp = await client.GetAsync($"/c/{shareCode}/video.mp4");

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.ToString().Should().Be("https://cdn.example.com/video.mp4");
    }

    [Fact]
    public async Task Video_UnknownCode_Returns404()
    {
        await _fx.ResetAsync();

        using var client = NoRedirectClient();
        var resp = await client.GetAsync("/c/unknown1/video.mp4");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Video_NotReady_Returns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, status: "processing");

        using var client = NoRedirectClient();
        var resp = await client.GetAsync($"/c/{shareCode}/video.mp4");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- GET /clip/{id} ----

    [Fact]
    public async Task ClipPreview_CrawlerUA_ReturnsOgHtml()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (id, shareCode) = await SeedClipAsync(userId, title: "Preview By Id");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");
        var resp = await client.GetAsync($"/clip/{id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("og:title");
        body.Should().Contain("Preview By Id");
        // Canonical stays the share-code URL regardless of which share path was crawled.
        body.Should().Contain($"{WebOrigin}/c/{shareCode}");
    }

    [Fact]
    public async Task ClipPreview_NonCrawlerUA_RedirectsToWebClipUrl()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (id, _) = await SeedClipAsync(userId);

        using var client = NoRedirectClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible)");
        var resp = await client.GetAsync($"/clip/{id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.ToString().Should().Be($"{WebOrigin}/clip/{id}");
    }

    [Fact]
    public async Task ClipPreview_PrivateClip_CrawlerGets404NoOgHtml()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (id, _) = await SeedClipAsync(userId, visibility: "private", title: "Secret By Id");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");
        var resp = await client.GetAsync($"/clip/{id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("Secret By Id");
    }

    // ---- GET /oembed ----

    [Fact]
    public async Task OEmbed_ShareCodeUrl_ReturnsRichJson()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("oembedauthor");
        var (_, shareCode) = await SeedClipAsync(userId, title: "OEmbed Clip");

        using var client = _factory!.CreateClient();
        var url = Uri.EscapeDataString($"{WebOrigin}/c/{shareCode}");
        var resp = await client.GetAsync($"/oembed?url={url}&format=json");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("version").GetString().Should().Be("1.0");
        body.GetProperty("type").GetString().Should().Be("link");
        body.GetProperty("title").GetString().Should().Be("OEmbed Clip");
        body.GetProperty("author_name").GetString().Should().Be("oembedauthor");
        body.GetProperty("author_url").GetString().Should().Be($"{WebOrigin}/user/oembedauthor");
        body.GetProperty("provider_name").GetString().Should().Be("GankedTV");
        body.GetProperty("provider_url").GetString().Should().Be(WebOrigin);
        body.GetProperty("thumbnail_url").GetString().Should().Be($"{WebOrigin}/c/{shareCode}/poster.jpg");
        body.GetProperty("thumbnail_width").GetInt32().Should().Be(1920);
        body.GetProperty("thumbnail_height").GetInt32().Should().Be(1080);
    }

    [Fact]
    public async Task OEmbed_ClipIdUrl_ResolvesSameClip()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (id, _) = await SeedClipAsync(userId, title: "By Id OEmbed");

        using var client = _factory!.CreateClient();
        var url = Uri.EscapeDataString($"{WebOrigin}/clip/{id}");
        var resp = await client.GetAsync($"/oembed?url={url}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("By Id OEmbed");
    }

    [Fact]
    public async Task OEmbed_MissingUrl_Returns400()
    {
        await _fx.ResetAsync();

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/oembed");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OEmbed_XmlFormat_Returns501()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId);

        using var client = _factory!.CreateClient();
        var url = Uri.EscapeDataString($"{WebOrigin}/c/{shareCode}");
        var resp = await client.GetAsync($"/oembed?url={url}&format=xml");

        resp.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task OEmbed_ForeignOrigin_Returns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId);

        using var client = _factory!.CreateClient();
        var url = Uri.EscapeDataString($"https://evil.example.com/c/{shareCode}");
        var resp = await client.GetAsync($"/oembed?url={url}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OEmbed_NonSharePath_Returns404()
    {
        await _fx.ResetAsync();

        using var client = _factory!.CreateClient();
        var url = Uri.EscapeDataString($"{WebOrigin}/user/somebody");
        var resp = await client.GetAsync($"/oembed?url={url}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OEmbed_PrivateClip_Returns404()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, visibility: "private", title: "Secret OEmbed");

        using var client = _factory!.CreateClient();
        var url = Uri.EscapeDataString($"{WebOrigin}/c/{shareCode}");
        var resp = await client.GetAsync($"/oembed?url={url}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("Secret OEmbed");
    }

    // ---- OG HTML enrichment on GET /c/{code} ----

    [Fact]
    public async Task ShareCode_OgHtml_UsesStableMediaUrlsAndRicherTags()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, title: "Rich Tags Clip");

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Discordbot/2.0");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("""<meta property="og:site_name" content="GankedTV" />""");
        body.Should().Contain("theme-color");
        // Media URLs are the stable share paths (fresh presign per fetch), not 1h presigned
        // URLs that would go dead an hour after the first crawl.
        body.Should().Contain($"{WebOrigin}/c/{shareCode}/poster.jpg");
        body.Should().Contain($"{WebOrigin}/c/{shareCode}/video.mp4");
        body.Should().NotContain("cdn.example.com");
        body.Should().Contain("""<meta property="og:image:width" content="1920" />""");
        body.Should().Contain("""<meta property="og:image:height" content="1080" />""");
        // oEmbed discovery so Discord can attribute author + provider.
        body.Should().Contain("application/json+oembed");
        var oembedUrl = Uri.EscapeDataString($"{WebOrigin}/c/{shareCode}");
        body.Should().Contain($"/oembed?url={oembedUrl}");
    }

    [Fact]
    public async Task ShareCode_AcceptJsonAnyCase_ReturnsJsonDetail()
    {
        // Media types are case-insensitive (RFC 9110); a client sending Application/JSON
        // must get the detail payload, not the human redirect.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (id, shareCode) = await SeedClipAsync(userId, title: "Case Insensitive Accept");

        using var client = NoRedirectClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "Application/JSON");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(id);
    }

    [Fact]
    public async Task ShareCode_ProxyHeader_TreatedAsCrawler()
    {
        // The web edge (Caddy) forwards crawler traffic with this header set. Trusting it —
        // in addition to the UA list — means a UA-list mismatch between Caddy and the API
        // degrades to serving OG HTML instead of bouncing crawlers through a redirect loop.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (_, shareCode) = await SeedClipAsync(userId, title: "Header Crawler");

        using var client = NoRedirectClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SomeNewBot/1.0");
        client.DefaultRequestHeaders.Add("X-GankedTV-Share-Preview", "1");
        var resp = await client.GetAsync($"/c/{shareCode}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Header Crawler");
    }
}
