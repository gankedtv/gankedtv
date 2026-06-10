using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresDiscovery")]
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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(
        string username = "owner",
        string? bio = null,
        string? avatarUrl = null) =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username,
            configure: u => { u.Bio = bio; u.AvatarUrl = avatarUrl; });

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

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
            VideoKey = $"clips/{userId}/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = ShareCodeGenerator.Next(),
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
    public async Task GetUser_AsOwner_IncludesUnlistedClips_ButNotHidden()
    {
        // Owner viewing their own profile sees public + unlisted (so they can find their
        // own private-link uploads), but NOT hidden clips — those are a moderation outcome
        // and need an explicit admin unhide before they reappear anywhere.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("alice");
        var now = DateTimeOffset.UtcNow;
        var pub = await SeedClipAsync(userId, now.AddSeconds(-1), title: "public");
        var unl = await SeedClipAsync(userId, now.AddSeconds(-2), visibility: "unlisted", title: "unlisted");
        await SeedClipAsync(userId, now.AddSeconds(-3), visibility: "hidden", title: "hidden");

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/users/alice");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var clipIds = body.GetProperty("clips").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        clipIds.Should().BeEquivalentTo(new[] { pub, unl });
    }

    [Fact]
    public async Task GetUser_AsForeignViewer_OmitsUnlistedAndHidden()
    {
        // Sanity: even an authenticated viewer who isn't the owner still only sees public.
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("alice");
        var (_, viewerToken) = await SeedUserAndIssueTokenAsync("bob");
        var now = DateTimeOffset.UtcNow;
        var pub = await SeedClipAsync(ownerId, now.AddSeconds(-1), title: "public");
        await SeedClipAsync(ownerId, now.AddSeconds(-2), visibility: "unlisted", title: "unlisted");
        await SeedClipAsync(ownerId, now.AddSeconds(-3), visibility: "hidden", title: "hidden");

        using var client = ClientWithBearer(viewerToken);
        var resp = await client.GetAsync("/users/alice");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var clipIds = body.GetProperty("clips").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        clipIds.Should().Equal(pub);
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
    public async Task GetUser_CapsClipsAt20()
    {
        // UsersEndpoints.UserClipsPageSize = 20. Seed 21 ready clips and assert the cap holds —
        // guards against accidental removal of the .Take(20) when someone adds cursor pagination.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("prolific");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 21; i++)
        {
            await SeedClipAsync(userId, now.AddSeconds(-i), title: $"clip-{i}");
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/users/prolific");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("clips").GetArrayLength().Should().Be(20);
    }

    [Fact]
    public async Task GetUser_DoesNotLeakClipsFromOtherUsers()
    {
        await _fx.ResetAsync();
        var (aliceId, _) = await SeedUserAndIssueTokenAsync("alice");
        var (bobId, _) = await SeedUserAndIssueTokenAsync("bob");
        var now = DateTimeOffset.UtcNow;
        var aliceClip = await SeedClipAsync(aliceId, now.AddSeconds(-1), title: "alice-clip");
        await SeedClipAsync(bobId, now.AddSeconds(-2), title: "bob-clip");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/users/alice");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var clipIds = body.GetProperty("clips").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        clipIds.Should().Equal(aliceClip);
    }

    [Fact]
    public async Task GetUser_ExcludesFailedStatusClips()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync("author");
        var ready = await SeedClipAsync(userId, DateTimeOffset.UtcNow.AddSeconds(-1), title: "ready");
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, status: "failed", title: "failed");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/users/author");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var clipIds = body.GetProperty("clips").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        clipIds.Should().Equal(ready);
    }

    [Theory]
    [InlineData("a%")]
    [InlineData("a_____")]
    [InlineData("%")]
    public async Task GetUser_UsernameWithSqlWildcard_Returns404(string wildcardUsername)
    {
        // Regression: an earlier implementation used EF.Functions.ILike which interpreted `%` and `_`
        // as wildcards, letting /users/a% match any username starting with "a" and legitimate `_` in
        // usernames become match-any. Fix uses case-insensitive equality instead.
        await _fx.ResetAsync();
        await SeedUserAndIssueTokenAsync("alice");
        await SeedUserAndIssueTokenAsync("alpha_one");

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/users/{Uri.EscapeDataString(wildcardUsername)}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
