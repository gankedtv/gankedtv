using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Notifications;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresSocial")]
public class FollowsEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public FollowsEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _storage = Substitute.For<IObjectStorageService>();
        // GET /users/{name} and /clips/feed both presign thumbnails; substitute returns a
        // fixed URL so the storage call doesn't crash during follow-feed tests.
        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns("https://example.test/thumb.jpg");
        _factory = new AuthApiFactory(_fx.ConnectionString, _storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username) =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<Guid> SeedClipAsync(Guid userId, DateTimeOffset createdAt)
    {
        var id = Guid.NewGuid();
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = "seed",
            VideoKey = $"clips/{userId}/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = "public",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- POST /users/{name}/follow ----

    [Fact]
    public async Task Follow_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        await SeedUserAndIssueTokenAsync("alice");
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsync("/users/alice/follow", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Follow_UnknownUser_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("alice");
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync("/users/nobody/follow", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Follow_Self_Returns400()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("alice");
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync("/users/alice/follow", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("self_follow");
    }

    [Fact]
    public async Task Follow_FirstTime_InsertsRow()
    {
        await _fx.ResetAsync();
        var (followerId, token) = await SeedUserAndIssueTokenAsync("follower");
        var (followeeId, _) = await SeedUserAndIssueTokenAsync("followee");
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync("/users/followee/follow", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = _fx.CreateContext();
        (await db.Follows.AnyAsync(f => f.FollowerId == followerId && f.FolloweeId == followeeId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Follow_ConcurrentSameUser_FinalStateIsConsistent()
    {
        // Parallel POSTs from the same follower: between the username lookup and the
        // INSERT, multiple requests may race into the insert path. The endpoint must
        // coalesce them into a single row via ON CONFLICT DO NOTHING — every response
        // 2xx, exactly one row in the table. Mirrors LikesEndpointsTests.Like_Concurrent
        // for the equivalent contract on follows.
        await _fx.ResetAsync();
        var (followerId, token) = await SeedUserAndIssueTokenAsync("follower");
        var (followeeId, _) = await SeedUserAndIssueTokenAsync("followee");

        const int parallelism = 8;
        var tasks = Enumerable.Range(0, parallelism).Select(async _ =>
        {
            using var client = ClientWithBearer(token);
            return await client.PostAsync("/users/followee/follow", content: null);
        }).ToArray();
        var responses = await Task.WhenAll(tasks);

        responses.Should().OnlyContain(r => r.IsSuccessStatusCode);

        await using var db = _fx.CreateContext();
        (await db.Follows.CountAsync(f => f.FollowerId == followerId && f.FolloweeId == followeeId))
            .Should().Be(1);
    }

    [Fact]
    public async Task Follow_FirstTime_RecordsNotificationForFollowee()
    {
        await _fx.ResetAsync();
        var (followerId, token) = await SeedUserAndIssueTokenAsync("follower");
        var (followeeId, _) = await SeedUserAndIssueTokenAsync("followee");
        using var client = ClientWithBearer(token);

        (await client.PostAsync("/users/followee/follow", content: null)).EnsureSuccessStatusCode();

        await using var db = _fx.CreateContext();
        var notif = await db.Notifications.SingleAsync();
        notif.Type.Should().Be(NotificationTypes.Follow);
        notif.RecipientId.Should().Be(followeeId);
        notif.ActorId.Should().Be(followerId);
        notif.ClipId.Should().BeNull();
        notif.CommentId.Should().BeNull();
    }

    [Fact]
    public async Task Follow_DuplicateFollow_OnlyRecordsOneNotification()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("follower");
        await SeedUserAndIssueTokenAsync("followee");
        using var client = ClientWithBearer(token);

        (await client.PostAsync("/users/followee/follow", content: null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/users/followee/follow", content: null)).EnsureSuccessStatusCode();

        await using var db = _fx.CreateContext();
        (await db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Follow_NotificationFailure_RollsBackFollowRow()
    {
        // Contract: INotificationService.RecordAsync runs inside the caller's transaction.
        // If recording throws, the follow row must NOT remain — otherwise re-follows can never
        // produce a notification (dedup is `inserted == 1`) and the event is lost forever.
        await _fx.ResetAsync();
        var (followerId, token) = await SeedUserAndIssueTokenAsync("follower");
        var (followeeId, _) = await SeedUserAndIssueTokenAsync("followee");

        var throwing = Substitute.For<INotificationService>();
        throwing.RecordAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated notification failure"));

        using var factory = _factory!.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<INotificationService>();
            s.AddScoped(_ => throwing);
        }));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsync("/users/followee/follow", content: null);

        resp.IsSuccessStatusCode.Should().BeFalse();
        await using var db = _fx.CreateContext();
        (await db.Follows.AnyAsync(f => f.FollowerId == followerId && f.FolloweeId == followeeId))
            .Should().BeFalse();
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Follow_Duplicate_IsIdempotent()
    {
        await _fx.ResetAsync();
        var (followerId, token) = await SeedUserAndIssueTokenAsync("follower");
        var (followeeId, _) = await SeedUserAndIssueTokenAsync("followee");
        using var client = ClientWithBearer(token);

        var first = await client.PostAsync("/users/followee/follow", content: null);
        var second = await client.PostAsync("/users/followee/follow", content: null);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = _fx.CreateContext();
        (await db.Follows.CountAsync(f => f.FollowerId == followerId && f.FolloweeId == followeeId))
            .Should().Be(1);
    }

    // ---- DELETE /users/{name}/follow ----

    [Fact]
    public async Task Unfollow_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        await SeedUserAndIssueTokenAsync("alice");
        using var client = _factory!.CreateClient();

        var resp = await client.DeleteAsync("/users/alice/follow");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unfollow_NeverFollowed_IsIdempotent()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("follower");
        await SeedUserAndIssueTokenAsync("followee");
        using var client = ClientWithBearer(token);

        var resp = await client.DeleteAsync("/users/followee/follow");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unfollow_AfterFollow_RemovesRow()
    {
        await _fx.ResetAsync();
        var (followerId, token) = await SeedUserAndIssueTokenAsync("follower");
        var (followeeId, _) = await SeedUserAndIssueTokenAsync("followee");
        using var client = ClientWithBearer(token);
        await client.PostAsync("/users/followee/follow", content: null);

        var resp = await client.DeleteAsync("/users/followee/follow");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = _fx.CreateContext();
        (await db.Follows.AnyAsync(f => f.FollowerId == followerId && f.FolloweeId == followeeId))
            .Should().BeFalse();
    }

    // ---- GET /users/{name}/followers + /following ----

    [Fact]
    public async Task ListFollowers_UnknownUser_Returns404()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/users/nobody/followers");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListFollowers_Paginates_AcrossTwoPages()
    {
        await _fx.ResetAsync();
        var (targetId, _) = await SeedUserAndIssueTokenAsync("target");
        var (a, _) = await SeedUserAndIssueTokenAsync("a");
        var (b, _) = await SeedUserAndIssueTokenAsync("b");

        // Insert follows directly so we can pin CreatedAt and assert order.
        var now = DateTimeOffset.UtcNow;
        await using (var db = _fx.CreateContext())
        {
            db.Follows.Add(new Follow { FollowerId = a, FolloweeId = targetId, CreatedAt = now.AddSeconds(-2) });
            db.Follows.Add(new Follow { FollowerId = b, FolloweeId = targetId, CreatedAt = now.AddSeconds(-1) });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var page1 = await client.GetFromJsonAsync<JsonElement>("/users/target/followers?limit=1");
        page1.GetProperty("items").GetArrayLength().Should().Be(1);
        page1.GetProperty("items")[0].GetProperty("username").GetString().Should().Be("b");
        var cursor = page1.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        var page2 = await client.GetFromJsonAsync<JsonElement>(
            $"/users/target/followers?limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        page2.GetProperty("items").GetArrayLength().Should().Be(1);
        page2.GetProperty("items")[0].GetProperty("username").GetString().Should().Be("a");
        page2.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListFollowers_GarbageCursor_FallsBackToFirstPage()
    {
        // Mirrors the clip feed's forgive-and-fall-back stance: a corrupted cursor
        // (truncated, wrong base64, mangled by a client) returns the first page rather
        // than 400-ing the pagination flow. KeysetCursor.TryParse handles the parse;
        // this exercises the wiring through the endpoint.
        await _fx.ResetAsync();
        var (targetId, _) = await SeedUserAndIssueTokenAsync("target");
        var (a, _) = await SeedUserAndIssueTokenAsync("a");
        await using (var db = _fx.CreateContext())
        {
            db.Follows.Add(new Follow { FollowerId = a, FolloweeId = targetId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>(
            "/users/target/followers?cursor=not-a-real-cursor");

        // First-page response: the row we seeded should be there, not silently dropped.
        page.GetProperty("items").GetArrayLength().Should().Be(1);
        page.GetProperty("items")[0].GetProperty("username").GetString().Should().Be("a");
    }

    [Fact]
    public async Task ListFollowing_ReturnsFolloweeUsernames()
    {
        await _fx.ResetAsync();
        var (sourceId, token) = await SeedUserAndIssueTokenAsync("source");
        await SeedUserAndIssueTokenAsync("targetA");
        await SeedUserAndIssueTokenAsync("targetB");

        using var client = ClientWithBearer(token);
        (await client.PostAsync("/users/targetA/follow", content: null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/users/targetB/follow", content: null)).EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<JsonElement>("/users/source/following");
        var names = page.GetProperty("items").EnumerateArray()
            .Select(u => u.GetProperty("username").GetString())
            .ToList();
        names.Should().BeEquivalentTo(new[] { "targetA", "targetB" });
    }

    // ---- Profile enrichment ----

    [Fact]
    public async Task Profile_Unauthenticated_FollowedByMeIsNull()
    {
        await _fx.ResetAsync();
        await SeedUserAndIssueTokenAsync("alice");
        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/users/alice");

        body.GetProperty("followerCount").GetInt32().Should().Be(0);
        body.GetProperty("followingCount").GetInt32().Should().Be(0);
        body.GetProperty("followedByMe").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Profile_Authenticated_ReflectsFollowState()
    {
        await _fx.ResetAsync();
        var (viewerId, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");

        // Non-follower view → false; counts zero.
        using (var c = ClientWithBearer(viewerToken))
        {
            var b = await c.GetFromJsonAsync<JsonElement>("/users/author");
            b.GetProperty("followedByMe").GetBoolean().Should().BeFalse();
            b.GetProperty("followerCount").GetInt32().Should().Be(0);
        }

        using (var c = ClientWithBearer(viewerToken))
        {
            (await c.PostAsync("/users/author/follow", content: null)).EnsureSuccessStatusCode();
            var b = await c.GetFromJsonAsync<JsonElement>("/users/author");
            b.GetProperty("followedByMe").GetBoolean().Should().BeTrue();
            b.GetProperty("followerCount").GetInt32().Should().Be(1);
        }
    }

    [Fact]
    public async Task Profile_OwnProfile_FollowedByMeIsNull()
    {
        // Viewing your own profile while authenticated: followedByMe is meaningless and
        // should be null so the UI hides the follow button on identity, not on the value.
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("me");
        using var client = ClientWithBearer(token);

        var body = await client.GetFromJsonAsync<JsonElement>("/users/me");

        body.GetProperty("followedByMe").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- Following feed (GET /clips/feed?source=following) ----

    [Fact]
    public async Task FollowingFeed_Unauthenticated_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/feed?source=following");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FollowingFeed_ReturnsOnlyFolloweesClips()
    {
        await _fx.ResetAsync();
        var (viewerId, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (alice, _) = await SeedUserAndIssueTokenAsync("alice");
        var (bob, _) = await SeedUserAndIssueTokenAsync("bob");
        var now = DateTimeOffset.UtcNow;
        var aliceClip = await SeedClipAsync(alice, now.AddSeconds(-1));
        await SeedClipAsync(bob, now.AddSeconds(-2)); // not followed → should be excluded

        // Viewer follows Alice but not Bob.
        await using (var db = _fx.CreateContext())
        {
            db.Follows.Add(new Follow { FollowerId = viewerId, FolloweeId = alice, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(viewerToken);
        var body = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=following");

        var clipIds = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        clipIds.Should().Equal(aliceClip);
    }

    [Fact]
    public async Task FollowingFeed_EmptyWhenFollowingNoone()
    {
        await _fx.ResetAsync();
        var (_, viewerToken) = await SeedUserAndIssueTokenAsync("viewer");
        var (alice, _) = await SeedUserAndIssueTokenAsync("alice");
        await SeedClipAsync(alice, DateTimeOffset.UtcNow);

        using var client = ClientWithBearer(viewerToken);
        var body = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=following");

        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Feed_DefaultsToPublic_WhenSourceMissingOrGarbage()
    {
        // Lenient source parsing: unknown values fall back to the public feed rather than
        // 400-ing — same spirit as the cursor decoder.
        await _fx.ResetAsync();
        var (alice, _) = await SeedUserAndIssueTokenAsync("alice");
        await SeedClipAsync(alice, DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();

        foreach (var url in new[] { "/clips/feed", "/clips/feed?source=public", "/clips/feed?source=garbage" })
        {
            var body = await client.GetFromJsonAsync<JsonElement>(url);
            body.GetProperty("items").GetArrayLength().Should().Be(1);
        }
    }
}
