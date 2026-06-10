using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Notifications;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresSocial")]
public class LikesEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public LikesEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "liker") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<Guid> SeedClipAsync(Guid userId, int initialLikeCount = 0)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = "likable",
            VideoKey = $"clips/{userId}/{id}.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = "public",
            LikeCount = initialLikeCount,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- POST /clips/{id}/like ----

    [Fact]
    public async Task Like_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsync($"/clips/{Guid.NewGuid()}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Like_ClipMissing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync($"/clips/{Guid.NewGuid()}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Like_FirstTime_InsertsRowAndIncrementsCounter()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (userId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/clips/{clipId}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likeCount").GetInt32().Should().Be(1);
        body.GetProperty("liked").GetBoolean().Should().BeTrue();

        await using var db = _fx.CreateContext();
        (await db.Likes.AnyAsync(l => l.UserId == userId && l.ClipId == clipId)).Should().BeTrue();
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.LikeCount).FirstAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Like_DoubleLike_IsIdempotent()
    {
        // Acceptance criterion: POST /like must be idempotent. Two sequential calls from the same
        // user leave exactly one row and like_count == 1.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (userId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);

        using var client = ClientWithBearer(token);
        var first = await client.PostAsync($"/clips/{clipId}/like", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsync($"/clips/{clipId}/like", content: null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likeCount").GetInt32().Should().Be(1);
        body.GetProperty("liked").GetBoolean().Should().BeTrue();

        await using var db = _fx.CreateContext();
        (await db.Likes.CountAsync(l => l.UserId == userId && l.ClipId == clipId)).Should().Be(1);
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.LikeCount).FirstAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Like_MultipleUsers_SumCorrectly()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token1) = await SeedUserAndIssueTokenAsync("fan1");
        var (_, token2) = await SeedUserAndIssueTokenAsync("fan2");
        var clipId = await SeedClipAsync(authorId);

        using (var c1 = ClientWithBearer(token1))
        {
            (await c1.PostAsync($"/clips/{clipId}/like", content: null)).EnsureSuccessStatusCode();
        }
        using (var c2 = ClientWithBearer(token2))
        {
            var resp = await c2.PostAsync($"/clips/{clipId}/like", content: null);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("likeCount").GetInt32().Should().Be(2);
        }

        await using var db = _fx.CreateContext();
        (await db.Likes.CountAsync(l => l.ClipId == clipId)).Should().Be(2);
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.LikeCount).FirstAsync()).Should().Be(2);
    }

    // ---- DELETE /clips/{id}/like ----

    [Fact]
    public async Task Unlike_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.DeleteAsync($"/clips/{Guid.NewGuid()}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unlike_ClipMissing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.DeleteAsync($"/clips/{Guid.NewGuid()}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unlike_WhenNeverLiked_IsIdempotent_Returns200()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("stranger");
        var clipId = await SeedClipAsync(authorId);

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/clips/{clipId}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likeCount").GetInt32().Should().Be(0);
        body.GetProperty("liked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Unlike_AfterLike_RemovesRow_DecrementsCounter()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (userId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);

        using var client = ClientWithBearer(token);
        (await client.PostAsync($"/clips/{clipId}/like", content: null)).EnsureSuccessStatusCode();

        var resp = await client.DeleteAsync($"/clips/{clipId}/like");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likeCount").GetInt32().Should().Be(0);
        body.GetProperty("liked").GetBoolean().Should().BeFalse();

        await using var db = _fx.CreateContext();
        (await db.Likes.AnyAsync(l => l.UserId == userId && l.ClipId == clipId)).Should().BeFalse();
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.LikeCount).FirstAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unlike_DoesNotDecrementBelowZero()
    {
        // Acceptance criterion: like_count must clamp at ≥ 0. Exercises the `WHERE like_count > 0`
        // predicate on the decrement by setting up the data-drift scenario it exists for: a Like
        // row present but LikeCount already 0 (e.g. manual DB repair, counter-drift bug). Without
        // the predicate the decrement would take the counter to -1.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (fanId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId, initialLikeCount: 0);
        await using (var seed = _fx.CreateContext())
        {
            seed.Likes.Add(new Like { UserId = fanId, ClipId = clipId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/clips/{clipId}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likeCount").GetInt32().Should().Be(0);

        await using var db = _fx.CreateContext();
        (await db.Likes.AnyAsync(l => l.UserId == fanId && l.ClipId == clipId)).Should().BeFalse();
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.LikeCount).FirstAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unlike_NeverLiked_CounterAtZero_StaysZero()
    {
        // Complements the clamp test above for the no-like-row path: idempotent unlike against
        // a clip at zero should not touch the counter.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("stranger");
        var clipId = await SeedClipAsync(authorId, initialLikeCount: 0);

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/clips/{clipId}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.LikeCount).FirstAsync()).Should().Be(0);
    }

    // ---- Notification side-effects ----

    [Fact]
    public async Task Like_RecordsNotificationForClipOwner()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (fanId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);

        using var client = ClientWithBearer(token);
        (await client.PostAsync($"/clips/{clipId}/like", content: null)).EnsureSuccessStatusCode();

        await using var db = _fx.CreateContext();
        var notif = await db.Notifications.SingleAsync();
        notif.Type.Should().Be(NotificationTypes.Like);
        notif.RecipientId.Should().Be(authorId);
        notif.ActorId.Should().Be(fanId);
        notif.ClipId.Should().Be(clipId);
    }

    [Fact]
    public async Task Like_OwnClip_DoesNotRecordNotification()
    {
        await _fx.ResetAsync();
        var (authorId, token) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(authorId);

        using var client = ClientWithBearer(token);
        (await client.PostAsync($"/clips/{clipId}/like", content: null)).EnsureSuccessStatusCode();

        await using var db = _fx.CreateContext();
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Like_DoubleLike_OnlyRecordsOneNotification()
    {
        // The endpoint already collapses re-likes into a single Like row via ON CONFLICT DO
        // NOTHING — the notification must follow the same guard.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);

        using var client = ClientWithBearer(token);
        (await client.PostAsync($"/clips/{clipId}/like", content: null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/clips/{clipId}/like", content: null)).EnsureSuccessStatusCode();

        await using var db = _fx.CreateContext();
        (await db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Unlike_DoesNotDeleteExistingLikeNotification()
    {
        // Per the issue: unliking must not erase the historical notification.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);

        using var client = ClientWithBearer(token);
        (await client.PostAsync($"/clips/{clipId}/like", content: null)).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/clips/{clipId}/like")).EnsureSuccessStatusCode();

        await using var db = _fx.CreateContext();
        (await db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Like_ConcurrentSameUser_FinalStateIsConsistent()
    {
        // Parallel POSTs from the same user: the `already` check races, so between 1..N requests
        // may reach the INSERT path and collide on the composite PK. The endpoint must coalesce
        // them into a single logical like — one row, counter == 1, all responses 2xx.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (userId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);

        const int parallelism = 8;
        var tasks = Enumerable.Range(0, parallelism).Select(async _ =>
        {
            using var client = ClientWithBearer(token);
            return await client.PostAsync($"/clips/{clipId}/like", content: null);
        }).ToArray();
        var responses = await Task.WhenAll(tasks);

        responses.Should().OnlyContain(r => r.IsSuccessStatusCode);

        await using var db = _fx.CreateContext();
        (await db.Likes.CountAsync(l => l.UserId == userId && l.ClipId == clipId)).Should().Be(1);
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.LikeCount).FirstAsync()).Should().Be(1);
    }
}
