using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Problems;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using GankedTV.Api.Validation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

/// <summary>
/// Golden-shape assertions for the RFC 7807 ProblemDetails + ValidationProblemDetails
/// envelope. One representative of each error category — status code/body shape alone is
/// cheaper and more maintainable than duplicating across every per-field case already
/// covered by the endpoint-specific test suites.
/// </summary>
[Collection("Postgres")]
public class ValidationShapeTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;

    public ValidationShapeTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        var storage = Substitute.For<IObjectStorageService>();
        _factory = new AuthApiFactory(_fx.ConnectionString, storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task PatchMe_BioTooLong_ReturnsValidationProblem()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("alice");
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync("/me", new { bio = new string('x', 501) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("errors").GetProperty("Bio").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PatchMe_NullBody_ReturnsValidationProblem()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("bob");
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync<object?>("/me", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("body").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PatchMe_Unauthenticated_ReturnsEnvelopeFor401()
    {
        // The JwtBearer challenge produces the 401; AddProblemDetails() shapes the empty
        // body into a ProblemDetails with status=401. We deliberately don't assert on
        // "code" here — framework-authored 401s don't carry our extensions.code, only
        // endpoint-authored ones do (e.g. Refresh's invalid_refresh, tested separately).
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PatchAsJsonAsync("/me", new { bio = "ok" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(401);
    }

    [Fact]
    public async Task PatchClip_NonOwner_Returns403ProblemWithForbiddenCode()
    {
        // Non-owner sees 403 with the unified ProblemDetails envelope (code=forbidden)
        // rather than an empty body from Results.Forbid().
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("owner");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("intruder");
        var clipId = await SeedClipAsync(ownerId);
        using var client = ClientWithBearer(otherToken);

        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "hijacked" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty(ProblemResults.CodeKey).GetString().Should().Be("forbidden");
    }

    [Fact]
    public async Task PatchMe_UsernameWhitespace_ReturnsProblemWithCode()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("carol");
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync("/me", new { username = "   " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty(ProblemResults.CodeKey).GetString().Should().Be("invalid_username");
    }

    [Fact]
    public async Task PatchClip_TitleTooLong_ReturnsValidationProblemWithTitleError()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync("owner");
        var clipId = await SeedClipAsync(ownerId);
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync(
            $"/clips/{clipId}",
            new { title = new string('a', ClipValidationLimits.MaxTitleLength + 1) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("Title").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateClip_MissingTitle_ReturnsValidationProblem()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("creator");
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips/", new { description = "no title" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("Title").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Refresh_EmptyToken_ReturnsValidationProblem()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refresh = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty("Refresh").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Refresh_InvalidToken_ReturnsProblemWithCode()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refresh = "not-a-real-token" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty(ProblemResults.CodeKey).GetString().Should().Be("invalid_refresh");
    }

    private async Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username)
    {
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = username,
                Email = $"{username}@example.com",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        using var scope = _factory!.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var token = jwt.Issue(new User { Id = id, Username = username, Email = $"{username}@example.com" });
        return (id, token);
    }

    private async Task<Guid> SeedClipAsync(Guid userId, string title = "seed")
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = title,
            VideoKey = $"clips/{id}.mp4",
            Status = "ready",
            Visibility = "public",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private HttpClient ClientWithBearer(string token)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
