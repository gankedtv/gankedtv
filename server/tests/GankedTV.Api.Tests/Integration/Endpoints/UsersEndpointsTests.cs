using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Auth.Jwt;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class UsersEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public UsersEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private async Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(
        string username = "owner",
        string? bio = null,
        string? avatarUrl = null)
    {
        var now = DateTimeOffset.UtcNow;
        Guid id;
        await using (var db = _fx.CreateContext())
        {
            var user = new User
            {
                Username = username,
                Email = $"{username}@example.com",
                Bio = bio,
                AvatarUrl = avatarUrl,
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

    private HttpClient ClientWithBearer(string token)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> SeedClipAsync(
        Guid userId,
        DateTimeOffset createdAt,
        string status = "ready",
        string visibility = "public",
        string? title = null)
    {
        var id = Guid.NewGuid();
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = title ?? "seed",
            VideoKey = $"clips/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            Status = status,
            Visibility = visibility,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task GetUser_NotFound_Returns404()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/users/nobody");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUser_ReturnsProfileWithReadyPublicClipsOnly()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("alice", bio: "hi", avatarUrl: "https://cdn/a.png");
        var now = DateTimeOffset.UtcNow;
        var ready1 = await SeedClipAsync(userId, now.AddSeconds(-1), title: "ready-1");
        var ready2 = await SeedClipAsync(userId, now.AddSeconds(-2), title: "ready-2");
        await SeedClipAsync(userId, now, status: "processing", title: "not-ready");
        await SeedClipAsync(userId, now, visibility: "unlisted", title: "unlisted");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/users/alice");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(userId);
        body.GetProperty("username").GetString().Should().Be("alice");
        body.GetProperty("bio").GetString().Should().Be("hi");
        body.GetProperty("avatarUrl").GetString().Should().Be("https://cdn/a.png");

        var clipIds = body.GetProperty("clips").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        clipIds.Should().Equal(ready1, ready2);
    }

    [Fact]
    public async Task GetUser_CaseInsensitiveUsername()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("CamelCase");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/users/camelcase");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(userId);
    }

    [Fact]
    public async Task GetUser_WithJwt_LikedByMeReflectsLikes()
    {
        await _fx.ResetAsync();
        var (viewerId, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var now = DateTimeOffset.UtcNow;
        var liked = await SeedClipAsync(authorId, now.AddSeconds(-1), title: "liked");
        var notLiked = await SeedClipAsync(authorId, now.AddSeconds(-2), title: "not-liked");

        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = viewerId, ClipId = liked, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(viewerToken);
        var resp = await client.GetAsync("/users/author");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var states = body.GetProperty("clips").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetGuid(), e => e.GetProperty("likedByMe").GetBoolean());
        states[liked].Should().BeTrue();
        states[notLiked].Should().BeFalse();
    }

    [Fact]
    public async Task GetUser_EmptyClips_StillReturnsProfile()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("lurker");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/users/lurker");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(userId);
        body.GetProperty("clips").GetArrayLength().Should().Be(0);
    }
}
