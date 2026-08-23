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
public class CommentLikesEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public CommentLikesEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username) =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<Guid> SeedClipAsync(Guid userId, string visibility = "public")
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
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedCommentAsync(
        Guid clipId,
        Guid userId,
        Guid? parentId = null,
        int likeCount = 0,
        DateTimeOffset? deletedAt = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Comments.Add(new Comment
        {
            Id = id,
            ClipId = clipId,
            UserId = userId,
            ParentId = parentId,
            Body = "hello",
            LikeCount = likeCount,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<int> LikeCountAsync(Guid commentId)
    {
        await using var db = _fx.CreateContext();
        return await db.Comments.Where(c => c.Id == commentId).Select(c => c.LikeCount).FirstAsync();
    }

    // ---- POST /comments/{id}/like ----

    [Fact]
    public async Task Like_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsync($"/comments/{Guid.NewGuid()}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Like_CommentMissing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsync($"/comments/{Guid.NewGuid()}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Like_RecordsRowAndBumpsCounter()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (fanId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/comments/{commentId}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likeCount").GetInt32().Should().Be(1);
        body.GetProperty("liked").GetBoolean().Should().BeTrue();
        await using var db = _fx.CreateContext();
        (await db.CommentLikes.AnyAsync(l => l.CommentId == commentId && l.UserId == fanId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Like_Twice_IsIdempotent()
    {
        // A double-click must not double-count: the ON CONFLICT insert collapses the second
        // request to zero rows, so the counter only moves once.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = ClientWithBearer(token);
        await client.PostAsync($"/comments/{commentId}/like", content: null);
        var second = await client.PostAsync($"/comments/{commentId}/like", content: null);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("likeCount")
            .GetInt32().Should().Be(1);
        (await LikeCountAsync(commentId)).Should().Be(1);
    }

    [Fact]
    public async Task Like_Reply_Works()
    {
        // The explicit ask in the issue: subcomments are likeable too, on the same endpoint.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);
        var parentId = await SeedCommentAsync(clipId, authorId);
        var replyId = await SeedCommentAsync(clipId, authorId, parentId: parentId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/comments/{replyId}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LikeCountAsync(replyId)).Should().Be(1);
        (await LikeCountAsync(parentId)).Should().Be(0);
    }

    [Fact]
    public async Task Like_DeletedComment_Returns404()
    {
        // A soft-deleted comment renders as `[deleted]` — there is no body left to endorse.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);
        var commentId = await SeedCommentAsync(clipId, authorId, deletedAt: DateTimeOffset.UtcNow);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/comments/{commentId}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = _fx.CreateContext();
        (await db.CommentLikes.AnyAsync(l => l.CommentId == commentId)).Should().BeFalse();
    }

    [Fact]
    public async Task Like_PrivateClip_NonOwnerReturns404()
    {
        // Matches the clip-like gate exactly: the same 404 a missing comment yields, so a
        // stranger can't tell "exists but private" from "gone".
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId, visibility: "private");
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/comments/{commentId}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Like_PrivateClip_OwnerReturns200()
    {
        await _fx.ResetAsync();
        var (authorId, token) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(authorId, visibility: "private");
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/comments/{commentId}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Like_NotifiesTheCommentAuthor()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("clipowner");
        var (commenterId, _) = await SeedUserAndIssueTokenAsync("commenter");
        var (fanId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(ownerId);
        var commentId = await SeedCommentAsync(clipId, commenterId);

        using var client = ClientWithBearer(token);
        await client.PostAsync($"/comments/{commentId}/like", content: null);

        await using var db = _fx.CreateContext();
        var notification = await db.Notifications
            .SingleAsync(n => n.Type == NotificationTypes.CommentLike);
        notification.RecipientId.Should().Be(commenterId, "the comment's author is notified, not the clip owner");
        notification.ActorId.Should().Be(fanId);
        notification.CommentId.Should().Be(commentId);
        notification.ClipId.Should().Be(clipId, "the notification row deep-links to the clip");
    }

    [Fact]
    public async Task Like_OwnComment_RecordsNoNotification()
    {
        await _fx.ResetAsync();
        var (authorId, token) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(authorId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsync($"/comments/{commentId}/like", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _fx.CreateContext();
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    // ---- DELETE /comments/{id}/like ----

    [Fact]
    public async Task Unlike_RemovesRowAndDecrementsCounter()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = ClientWithBearer(token);
        await client.PostAsync($"/comments/{commentId}/like", content: null);
        var resp = await client.DeleteAsync($"/comments/{commentId}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("likeCount").GetInt32().Should().Be(0);
        body.GetProperty("liked").GetBoolean().Should().BeFalse();
        await using var db = _fx.CreateContext();
        (await db.CommentLikes.AnyAsync(l => l.CommentId == commentId)).Should().BeFalse();
    }

    [Fact]
    public async Task Unlike_WithoutAnExistingLike_IsANoOp()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);
        var commentId = await SeedCommentAsync(clipId, authorId, likeCount: 3);

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/comments/{commentId}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("likeCount")
            .GetInt32().Should().Be(3, "someone else's likes are not touched");
    }

    [Fact]
    public async Task Unlike_DoesNotDecrementBelowZero()
    {
        // Guards the counter against data drift: a like row with a counter already at zero
        // must clamp rather than go negative.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (fanId, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);
        var commentId = await SeedCommentAsync(clipId, authorId);
        await using (var db = _fx.CreateContext())
        {
            db.CommentLikes.Add(new CommentLike { UserId = fanId, CommentId = commentId });
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/comments/{commentId}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LikeCountAsync(commentId)).Should().Be(0);
    }

    [Fact]
    public async Task Unlike_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.DeleteAsync($"/comments/{Guid.NewGuid()}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unlike_PrivateClip_NonOwnerReturns404()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId, visibility: "private");
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = ClientWithBearer(token);
        var resp = await client.DeleteAsync($"/comments/{commentId}/like");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReLike_AfterUnlike_RecordsAgain()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("fan");
        var clipId = await SeedClipAsync(authorId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = ClientWithBearer(token);
        await client.PostAsync($"/comments/{commentId}/like", content: null);
        await client.DeleteAsync($"/comments/{commentId}/like");
        var resp = await client.PostAsync($"/comments/{commentId}/like", content: null);

        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("likeCount")
            .GetInt32().Should().Be(1);
    }
}
