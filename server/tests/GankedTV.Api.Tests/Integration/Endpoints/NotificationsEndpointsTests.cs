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
public class NotificationsEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public NotificationsEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "recipient") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<Guid> SeedClipAsync(Guid userId)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = "seed",
            VideoKey = $"clips/{userId}/{id}.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = "public",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedNotificationAsync(
        Guid recipientId,
        Guid actorId,
        string type,
        Guid? clipId = null,
        Guid? commentId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? readAt = null)
    {
        var id = Guid.NewGuid();
        var when = createdAt ?? DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Notifications.Add(new Notification
        {
            Id = id,
            RecipientId = recipientId,
            ActorId = actorId,
            Type = type,
            ClipId = clipId,
            CommentId = commentId,
            CreatedAt = when,
            ReadAt = readAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- GET /me/notifications ----

    [Fact]
    public async Task List_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/me/notifications");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_OnlyOwnNotifications_NewestFirst()
    {
        await _fx.ResetAsync();
        var (recipientId, token) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");
        var (otherId, _) = await SeedUserAndIssueTokenAsync("other");

        var older = await SeedNotificationAsync(
            recipientId, actorId, NotificationTypes.Follow,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = await SeedNotificationAsync(
            recipientId, actorId, NotificationTypes.Follow,
            createdAt: DateTimeOffset.UtcNow);
        // Belongs to another user — must NOT appear in this caller's list.
        await SeedNotificationAsync(
            otherId, actorId, NotificationTypes.Follow,
            createdAt: DateTimeOffset.UtcNow);

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/me/notifications");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].GetProperty("id").GetGuid().Should().Be(newer);
        items[1].GetProperty("id").GetGuid().Should().Be(older);
    }

    [Fact]
    public async Task List_HydratesActorClipAndComment()
    {
        await _fx.ResetAsync();
        var (recipientId, token) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");
        var clipId = await SeedClipAsync(recipientId);

        var commentId = Guid.NewGuid();
        await using (var db = _fx.CreateContext())
        {
            db.Comments.Add(new Comment
            {
                Id = commentId,
                ClipId = clipId,
                UserId = actorId,
                Body = "nice play",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Comment, clipId, commentId);

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync("/me/notifications");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items")[0];
        item.GetProperty("type").GetString().Should().Be("comment");
        item.GetProperty("actor").GetProperty("username").GetString().Should().Be("actor");
        item.GetProperty("clip").GetProperty("id").GetGuid().Should().Be(clipId);
        item.GetProperty("comment").GetProperty("body").GetString().Should().Be("nice play");
    }

    [Fact]
    public async Task List_CursorPaginates()
    {
        await _fx.ResetAsync();
        var (recipientId, token) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await SeedNotificationAsync(
                recipientId, actorId, NotificationTypes.Follow,
                createdAt: now.AddSeconds(-i));
        }

        using var client = ClientWithBearer(token);
        var firstPage = await client.GetFromJsonAsync<JsonElement>("/me/notifications?limit=2");
        firstPage.GetProperty("items").GetArrayLength().Should().Be(2);
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNull();

        var secondPage = await client.GetFromJsonAsync<JsonElement>(
            $"/me/notifications?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        secondPage.GetProperty("items").GetArrayLength().Should().Be(2);

        var thirdPage = await client.GetFromJsonAsync<JsonElement>(
            $"/me/notifications?limit=2&cursor={Uri.EscapeDataString(secondPage.GetProperty("nextCursor").GetString()!)}");
        thirdPage.GetProperty("items").GetArrayLength().Should().Be(1);
        thirdPage.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- GET /me/notifications/unread-count ----

    [Fact]
    public async Task UnreadCount_OnlyCountsUnreadForCaller()
    {
        await _fx.ResetAsync();
        var (recipientId, token) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");
        var (otherId, _) = await SeedUserAndIssueTokenAsync("other");

        await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Follow);
        await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Follow);
        // Read — must be excluded.
        await SeedNotificationAsync(
            recipientId, actorId, NotificationTypes.Follow,
            readAt: DateTimeOffset.UtcNow);
        // Belongs to someone else.
        await SeedNotificationAsync(otherId, actorId, NotificationTypes.Follow);

        using var client = ClientWithBearer(token);
        var body = await client.GetFromJsonAsync<JsonElement>("/me/notifications/unread-count");

        body.GetProperty("count").GetInt32().Should().Be(2);
    }

    // ---- POST /me/notifications/read ----

    [Fact]
    public async Task MarkAllRead_MarksOnlyCallersUnread_ReturnsCount()
    {
        await _fx.ResetAsync();
        var (recipientId, token) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");
        var (otherId, _) = await SeedUserAndIssueTokenAsync("other");

        await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Follow);
        await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Follow);
        var othersUnread = await SeedNotificationAsync(otherId, actorId, NotificationTypes.Follow);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync("/me/notifications/read", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("marked").GetInt32().Should().Be(2);

        await using var db = _fx.CreateContext();
        (await db.Notifications.CountAsync(n => n.RecipientId == recipientId && n.ReadAt == null))
            .Should().Be(0);
        // Other user's row is untouched.
        (await db.Notifications.Where(n => n.Id == othersUnread).Select(n => n.ReadAt).FirstAsync())
            .Should().BeNull();
    }

    // ---- POST /me/notifications/{id}/read ----

    [Fact]
    public async Task MarkOneRead_Success_Returns204()
    {
        await _fx.ResetAsync();
        var (recipientId, token) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");
        var notifId = await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Follow);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/me/notifications/{notifId}/read", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var db = _fx.CreateContext();
        (await db.Notifications.Where(n => n.Id == notifId).Select(n => n.ReadAt).FirstAsync())
            .Should().NotBeNull();
    }

    [Fact]
    public async Task MarkOneRead_AlreadyRead_Returns204_AndPreservesOriginalReadAt()
    {
        // Idempotent re-mark: a second call must NOT overwrite the original read timestamp.
        // Important if read_at is ever used for analytics (time-to-read), and matches the
        // 204-on-repeat semantics that the dropdown depends on for offline re-syncing.
        await _fx.ResetAsync();
        var (recipientId, token) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");
        var originalReadAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notifId = await SeedNotificationAsync(
            recipientId, actorId, NotificationTypes.Follow, readAt: originalReadAt);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/me/notifications/{notifId}/read", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var db = _fx.CreateContext();
        var storedReadAt = await db.Notifications
            .Where(n => n.Id == notifId)
            .Select(n => n.ReadAt)
            .FirstAsync();
        storedReadAt.Should().NotBeNull();
        // Timestamp tolerance: Postgres stores µs precision, the seeded value comes from
        // DateTimeOffset (100ns ticks), so compare with a sub-millisecond budget.
        storedReadAt!.Value.Should().BeCloseTo(originalReadAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task MarkOneRead_NotMine_Returns404()
    {
        // Cross-user safety: caller should never be able to mutate another user's row.
        await _fx.ResetAsync();
        var (recipientId, _) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("other");
        var notifId = await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Follow);

        using var client = ClientWithBearer(otherToken);
        var resp = await client.PostAsync($"/me/notifications/{notifId}/read", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var db = _fx.CreateContext();
        (await db.Notifications.Where(n => n.Id == notifId).Select(n => n.ReadAt).FirstAsync())
            .Should().BeNull();
    }

    [Fact]
    public async Task MarkOneRead_Missing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync($"/me/notifications/{Guid.NewGuid()}/read", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Cascade behaviour ----

    [Fact]
    public async Task DeletingClip_CascadesNotifications()
    {
        await _fx.ResetAsync();
        var (recipientId, _) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");
        var clipId = await SeedClipAsync(recipientId);
        var notifId = await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Like, clipId);

        await using (var db = _fx.CreateContext())
        {
            await db.Clips.Where(c => c.Id == clipId).ExecuteDeleteAsync();
        }

        await using var verify = _fx.CreateContext();
        (await verify.Notifications.AnyAsync(n => n.Id == notifId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingUser_CascadesAsRecipientAndActor()
    {
        await _fx.ResetAsync();
        var (recipientId, _) = await SeedUserAndIssueTokenAsync("recipient");
        var (actorId, _) = await SeedUserAndIssueTokenAsync("actor");

        var asRecipient = await SeedNotificationAsync(recipientId, actorId, NotificationTypes.Follow);

        // Flip the roles: same actor is now the recipient of a row authored by the soon-to-be-
        // deleted user, so we can prove the cascade fires from either side of the FK.
        var asActor = await SeedNotificationAsync(actorId, recipientId, NotificationTypes.Follow);

        await using (var db = _fx.CreateContext())
        {
            await db.Users.Where(u => u.Id == recipientId).ExecuteDeleteAsync();
        }

        await using var verify = _fx.CreateContext();
        (await verify.Notifications.AnyAsync(n => n.Id == asRecipient)).Should().BeFalse();
        (await verify.Notifications.AnyAsync(n => n.Id == asActor)).Should().BeFalse();
    }
}
