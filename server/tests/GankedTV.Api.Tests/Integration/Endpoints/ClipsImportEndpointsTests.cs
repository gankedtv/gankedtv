using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class ClipsImportEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ClipsImportEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "importer") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    [Fact]
    public async Task Import_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/clips/import", new { url = "https://medal.tv/x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Import_AllowedHost_CreatesImportingRow()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips/import", new
        {
            url = "https://medal.tv/clips/abc123",
            title = "epic frag",
            visibility = "public",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        body.GetProperty("status").GetString().Should().Be(ClipStatuses.Importing);

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == id);
        clip.UserId.Should().Be(userId);
        clip.Status.Should().Be(ClipStatuses.Importing);
        clip.Title.Should().Be("epic frag");
        clip.ImportSourceUrl.Should().Be("https://medal.tv/clips/abc123");
    }

    [Fact]
    public async Task Import_NoTitle_PlaceholderApplied()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips/import", new
        {
            url = "https://www.youtube.com/watch?v=abc",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        await using var db = _fx.CreateContext();
        var clip = await db.Clips.AsNoTracking().SingleAsync(c => c.Id == id);
        clip.Title.Should().Be("Importing…");
    }

    [Fact]
    public async Task Import_UnsupportedHost_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips/import", new
        {
            url = "https://vimeo.com/clip/xyz",
            title = "elsewhere",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("unsupported_host");
    }

    [Fact]
    public async Task Import_HttpScheme_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips/import", new
        {
            url = "http://www.youtube.com/watch?v=x",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_url");
    }

    [Fact]
    public async Task Import_NullBody_Returns400ValidationProblem()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync("/clips/import",
            new StringContent("null", System.Text.Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Status_Owner_ReturnsCurrentStatus()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips/import", new
        {
            url = "https://medal.tv/clips/x",
        });
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var statusResp = await client.GetAsync($"/clips/{id}/status");
        statusResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await statusResp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(id);
        body.GetProperty("status").GetString().Should().Be(ClipStatuses.Importing);
        body.GetProperty("shareCode").GetString().Should().NotBeNullOrEmpty();

        // Discard 'userId' warning suppression — used implicitly to scope ownership.
        _ = userId;
    }

    [Fact]
    public async Task Status_OtherUser_Returns404()
    {
        await _fx.ResetAsync();
        var (_, ownerToken) = await SeedUserAndIssueTokenAsync("owner");
        using (var ownerClient = ClientWithBearer(ownerToken))
        {
            var resp = await ownerClient.PostAsJsonAsync("/clips/import", new
            {
                url = "https://medal.tv/clips/x",
            });
            resp.EnsureSuccessStatusCode();
            var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            var (_, otherToken) = await SeedUserAndIssueTokenAsync("other");
            using var otherClient = ClientWithBearer(otherToken);
            var statusResp = await otherClient.GetAsync($"/clips/{id}/status");
            statusResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Status_FailedClipWithReason_ExposesFailureReasonAndDuration()
    {
        // The wizard renders "Clip is X — max allowed is Y" only when the status endpoint
        // returns failureReason + durationSecs. This locks in that contract end-to-end so
        // the worker's MarkFailedAsync writes don't silently stop being surfaced.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedFailedClipAsync(userId,
            failureReason: ClipFailureReasons.SourceTooLong, durationSecs: 240);

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync($"/clips/{clipId}/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be(ClipStatuses.Failed);
        body.GetProperty("failureReason").GetString().Should().Be(ClipFailureReasons.SourceTooLong);
        body.GetProperty("durationSecs").GetInt32().Should().Be(240);
        body.GetProperty("maxClipDurationSecs").GetInt32().Should().Be(120);
    }

    // --- Preview endpoint --------------------------------------------------------------

    [Fact]
    public async Task Preview_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/clips/import/preview", new { url = "https://medal.tv/x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Preview_UnsupportedHost_Returns400_WithoutInvokingSource()
    {
        // URL allow-list runs BEFORE the import source is touched. Verifies callers can't
        // burn yt-dlp invocations on disallowed hosts by spamming /preview.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips/import/preview", new { url = "https://vimeo.com/x" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("unsupported_host");
        // ProbeAsync was never wired in this factory (the default uses YtDlpImportSource),
        // and the host check short-circuits before yt-dlp would be invoked — so the test
        // doesn't need a custom source registration to assert correctness of the gate.
    }

    [Fact]
    public async Task Preview_AllowedUrl_ReturnsMetadata()
    {
        // End-to-end test of the preview endpoint with a substitute import source so the
        // test doesn't hit YouTube/Medal. Replaces IClipImportSource via the factory's
        // configureServices hook.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();

        var source = Substitute.For<GankedTV.Api.Services.Media.Import.IClipImportSource>();
        source.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GankedTV.Api.Services.Media.Import.ImportedMedia(
                "Probed Title", 42, 1280, 720, "https://i.ytimg.com/vi/x/hqdefault.jpg"));

        await using var factory = new AuthApiFactory(
            _fx.ConnectionString,
            _storage,
            configureServices: s =>
            {
                s.RemoveAll<GankedTV.Api.Services.Media.Import.IClipImportSource>();
                s.AddSingleton(source);
            });
        using var client = AuthTestHelpers.CreateBearerClient(factory, token);

        var resp = await client.PostAsJsonAsync("/clips/import/preview",
            new { url = "https://www.youtube.com/watch?v=abc" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Probed Title");
        body.GetProperty("durationSecs").GetInt32().Should().Be(42);
        body.GetProperty("thumbnailUrl").GetString().Should().Be("https://i.ytimg.com/vi/x/hqdefault.jpg");
        body.GetProperty("maxClipDurationSecs").GetInt32().Should().Be(120);
    }

    // --- Helpers -----------------------------------------------------------------------

    private async Task<Guid> SeedFailedClipAsync(Guid userId, string failureReason, short? durationSecs)
    {
        await using var db = _fx.CreateContext();
        var clip = new Clip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "failed seed",
            VideoKey = $"{userId}/x.mp4",
            ShareCode = GankedTV.Api.Clips.ShareCodeGenerator.Next(),
            Status = ClipStatuses.Failed,
            Visibility = "public",
            FailureReason = failureReason,
            DurationSecs = durationSecs,
            ImportSourceUrl = "https://www.youtube.com/watch?v=abc",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Clips.Add(clip);
        await db.SaveChangesAsync();
        return clip.Id;
    }
}
