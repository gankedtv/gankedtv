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
}
