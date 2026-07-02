using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Notifications;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using GankedTV.Api.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresSocial")]
public class CommentsEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public CommentsEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "owner") =>
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
        string body = "hello",
        Guid? parentId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? deletedAt = null)
    {
        var id = Guid.NewGuid();
        var seeded = createdAt ?? DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Comments.Add(new Comment
        {
            Id = id,
            ClipId = clipId,
            UserId = userId,
            ParentId = parentId,
            Body = body,
            CreatedAt = seeded,
            UpdatedAt = seeded,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- POST /clips/{id}/comments ----

    [Fact]
    public async Task Create_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsJsonAsync($"/clips/{Guid.NewGuid()}/comments", new { body = "hi" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_ClipMissing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync($"/clips/{Guid.NewGuid()}/comments", new { body = "hi" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_PrivateClip_NonOwnerReturns404()
    {
        // A private clip must be indistinguishable from a missing one to non-owners.
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync();
        var (_, strangerToken) = await SeedUserAndIssueTokenAsync("stranger");
        var clipId = await SeedClipAsync(ownerId, visibility: "private");

        using var client = ClientWithBearer(strangerToken);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "hi" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_PrivateClip_OwnerReturns201()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, visibility: "private");

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "note to self" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_EmptyBody_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WhitespaceBody_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        using var client = ClientWithBearer(token);

        // [Required] trims before validating, so a whitespace-only body is rejected as a
        // validation problem (400) the same as an empty one.
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "   " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_MaxBodyLength_Returns201()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        using var client = ClientWithBearer(token);

        var body = new string('x', CommentValidationLimits.MaxBodyLength);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_ExceedsMaxBodyLength_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        using var client = ClientWithBearer(token);

        var body = new string('x', CommentValidationLimits.MaxBodyLength + 1);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_TopLevel_Returns201WithItem()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(userId);
        using var client = ClientWithBearer(token);

        var before = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "  first!  " });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await resp.Content.ReadFromJsonAsync<JsonElement>();
        item.GetProperty("body").GetString().Should().Be("first!"); // trimmed
        item.GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("replyCount").GetInt32().Should().Be(0);
        item.GetProperty("deleted").GetBoolean().Should().BeFalse();
        item.GetProperty("author").GetProperty("username").GetString().Should().Be("author");
        // Lock down that the response carries a DB-generated createdAt (not the default value)
        // — easy to regress if HasDefaultValueSql wiring is ever changed.
        var createdAt = item.GetProperty("createdAt").GetDateTimeOffset();
        createdAt.Should().BeOnOrAfter(before.AddSeconds(-1));
        createdAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Create_OnOthersClip_RecordsNotificationForClipOwner()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("author");
        var (commenterId, token) = await SeedUserAndIssueTokenAsync("commenter");
        var clipId = await SeedClipAsync(ownerId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "great clip" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var db = _fx.CreateContext();
        var notif = await db.Notifications.SingleAsync();
        notif.Type.Should().Be(NotificationTypes.Comment);
        notif.RecipientId.Should().Be(ownerId);
        notif.ActorId.Should().Be(commenterId);
        notif.ClipId.Should().Be(clipId);
        notif.CommentId.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_NotificationFailure_RollsBackCommentRow()
    {
        // Contract: INotificationService.RecordAsync runs inside the caller's transaction.
        // If recording throws, the comment row must NOT remain — otherwise commenters see a
        // failed request but their comment is silently published without notifying the owner.
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, token) = await SeedUserAndIssueTokenAsync("commenter");
        var clipId = await SeedClipAsync(ownerId);

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
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "doomed" });

        resp.IsSuccessStatusCode.Should().BeFalse();
        await using var db = _fx.CreateContext();
        (await db.Comments.AnyAsync(c => c.ClipId == clipId)).Should().BeFalse();
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_OnOwnClip_DoesNotRecordNotification()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(userId);

        using var client = ClientWithBearer(token);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "self" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var db = _fx.CreateContext();
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_Reply_Returns201()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var parentId = await SeedCommentAsync(clipId, userId, "top");
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync(
            $"/clips/{clipId}/comments", new { body = "reply", parentId });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await resp.Content.ReadFromJsonAsync<JsonElement>();
        item.GetProperty("parentId").GetGuid().Should().Be(parentId);
    }

    [Fact]
    public async Task Create_ReplyToReply_Returns400InvalidParent()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var topId = await SeedCommentAsync(clipId, userId, "top");
        var replyId = await SeedCommentAsync(clipId, userId, "reply", parentId: topId);
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync(
            $"/clips/{clipId}/comments", new { body = "nested", parentId = replyId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_parent");
    }

    [Fact]
    public async Task Create_ParentFromDifferentClip_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipA = await SeedClipAsync(userId);
        var clipB = await SeedClipAsync(userId);
        var parentOnB = await SeedCommentAsync(clipB, userId, "on B");
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync(
            $"/clips/{clipA}/comments", new { body = "x", parentId = parentOnB });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_parent");
    }

    [Fact]
    public async Task Create_ParentMissing_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync(
            $"/clips/{clipId}/comments", new { body = "x", parentId = Guid.NewGuid() });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- GET /clips/{id}/comments ----

    [Fact]
    public async Task List_Empty_Returns200WithNoItems()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync($"/clips/{clipId}/comments");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_PrivateClip_NonOwnerReturns200Empty()
    {
        // A private clip must be indistinguishable from a nonexistent one: a missing clip
        // 200s with an empty page, so a private-not-mine clip does too (never 404, which would
        // confirm existence). The seeded comment must not leak, so items stays empty.
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync();
        var (_, strangerToken) = await SeedUserAndIssueTokenAsync("stranger");
        var clipId = await SeedClipAsync(ownerId, visibility: "private");
        await SeedCommentAsync(clipId, ownerId, "secret note");

        using var anonymous = _factory!.CreateClient();
        using var stranger = ClientWithBearer(strangerToken);
        var anonResp = await anonymous.GetAsync($"/clips/{clipId}/comments");
        var strangerResp = await stranger.GetAsync($"/clips/{clipId}/comments");

        anonResp.StatusCode.Should().Be(HttpStatusCode.OK);
        strangerResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").GetArrayLength().Should().Be(0);
        (await strangerResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_PrivateClip_OwnerReturns200WithComments()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, visibility: "private");
        await SeedCommentAsync(clipId, ownerId, "note to self");

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync($"/clips/{clipId}/comments");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ListReplies_PrivateClip_NonOwnerReturns200Empty()
    {
        // The reply route is keyed by comment id, so the private gate has to travel
        // through the parent comment's clip. Like ListComments, it returns the same empty
        // page a nonexistent comment id yields rather than 404-ing (existence oracle).
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, visibility: "private");
        var top = await SeedCommentAsync(clipId, ownerId, "top");
        await SeedCommentAsync(clipId, ownerId, "reply", parentId: top);

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync($"/comments/{top}/replies");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListReplies_PrivateClip_OwnerReturns200()
    {
        await _fx.ResetAsync();
        var (ownerId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(ownerId, visibility: "private");
        var top = await SeedCommentAsync(clipId, ownerId, "top");
        await SeedCommentAsync(clipId, ownerId, "reply", parentId: top);

        using var client = ClientWithBearer(token);
        var resp = await client.GetAsync($"/comments/{top}/replies");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task List_ReturnsTopLevelWithInlineRepliesAndCount()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var baseTime = DateTimeOffset.UtcNow;
        var top = await SeedCommentAsync(clipId, userId, "top", createdAt: baseTime);
        // 4 replies — preview caps at 3 but replyCount reflects all live replies.
        for (var i = 0; i < 4; i++)
            await SeedCommentAsync(clipId, userId, $"r{i}", parentId: top, createdAt: baseTime.AddSeconds(i + 1));
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync($"/clips/{clipId}/comments");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var first = items[0];
        first.GetProperty("replyCount").GetInt32().Should().Be(4);
        first.GetProperty("replies").GetArrayLength().Should().Be(3);
        // Inline preview is oldest-first.
        first.GetProperty("replies")[0].GetProperty("body").GetString().Should().Be("r0");
        // With more replies than fit in the preview, the response carries a cursor that pages
        // forward from the last preview row — letting the UI fetch r3 without re-fetching r0..r2.
        var nextCursor = first.GetProperty("repliesNextCursor").GetString();
        nextCursor.Should().NotBeNullOrEmpty();
        var nextPage = await (await client.GetAsync($"/comments/{top}/replies?cursor={nextCursor}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        nextPage.GetProperty("items").GetArrayLength().Should().Be(1);
        nextPage.GetProperty("items")[0].GetProperty("body").GetString().Should().Be("r3");
    }

    [Fact]
    public async Task List_RepliesFitInPreview_RepliesNextCursorIsNull()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var baseTime = DateTimeOffset.UtcNow;
        var top = await SeedCommentAsync(clipId, userId, "top", createdAt: baseTime);
        // 2 replies fit in the preview (cap 3) — the response should not surface a "more" cursor.
        for (var i = 0; i < 2; i++)
            await SeedCommentAsync(clipId, userId, $"r{i}", parentId: top, createdAt: baseTime.AddSeconds(i + 1));
        using var client = _factory!.CreateClient();

        var body = await (await client.GetAsync($"/clips/{clipId}/comments"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var first = body.GetProperty("items")[0];
        first.GetProperty("replyCount").GetInt32().Should().Be(2);
        first.GetProperty("repliesNextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_SoftDeletedTopLevelWithReplies_StillAppearsAsDeleted()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var top = await SeedCommentAsync(clipId, userId, "secret", deletedAt: DateTimeOffset.UtcNow);
        await SeedCommentAsync(clipId, userId, "reply", parentId: top);
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync($"/clips/{clipId}/comments");

        var items = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("deleted").GetBoolean().Should().BeTrue();
        items[0].GetProperty("body").ValueKind.Should().Be(JsonValueKind.Null);
        items[0].GetProperty("replyCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task List_SoftDeletedTopLevelWithoutReplies_Excluded()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        await SeedCommentAsync(clipId, userId, "gone", deletedAt: DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync($"/clips/{clipId}/comments");

        var items = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        items.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_Paginates_WithCursor()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
            await SeedCommentAsync(clipId, userId, $"c{i}", createdAt: baseTime.AddSeconds(i));
        using var client = _factory!.CreateClient();

        var firstPage = await (await client.GetAsync($"/clips/{clipId}/comments?limit=2"))
            .Content.ReadFromJsonAsync<JsonElement>();
        firstPage.GetProperty("items").GetArrayLength().Should().Be(2);
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        var secondPage = await (await client.GetAsync($"/clips/{clipId}/comments?limit=2&cursor={cursor}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        secondPage.GetProperty("items").GetArrayLength().Should().Be(1);
        secondPage.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_LimitOutOfRange_IsClamped()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
            await SeedCommentAsync(clipId, userId, $"c{i}", createdAt: baseTime.AddSeconds(i));
        using var client = _factory!.CreateClient();

        // limit=0 clamps up to the minimum (1) — the endpoint shouldn't 400 on degenerate input.
        var low = await (await client.GetAsync($"/clips/{clipId}/comments?limit=0"))
            .Content.ReadFromJsonAsync<JsonElement>();
        low.GetProperty("items").GetArrayLength().Should().Be(1);
        low.GetProperty("nextCursor").GetString().Should().NotBeNullOrEmpty();

        // limit=10000 clamps down to MaxLimit (100); seeded data is well under that, so we expect
        // the endpoint to accept the request and return everything without paging.
        var high = await (await client.GetAsync($"/clips/{clipId}/comments?limit=10000"))
            .Content.ReadFromJsonAsync<JsonElement>();
        high.GetProperty("items").GetArrayLength().Should().Be(3);
        high.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- GET /comments/{id}/replies ----

    [Fact]
    public async Task ListReplies_ReturnsRepliesOldestFirst_AndPaginates()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var top = await SeedCommentAsync(clipId, userId, "top");
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
            await SeedCommentAsync(clipId, userId, $"r{i}", parentId: top, createdAt: baseTime.AddSeconds(i));
        // A deleted reply must be filtered out.
        await SeedCommentAsync(clipId, userId, "dead", parentId: top,
            createdAt: baseTime.AddSeconds(10), deletedAt: DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();

        var firstPage = await (await client.GetAsync($"/comments/{top}/replies?limit=2"))
            .Content.ReadFromJsonAsync<JsonElement>();
        firstPage.GetProperty("items").GetArrayLength().Should().Be(2);
        firstPage.GetProperty("items")[0].GetProperty("body").GetString().Should().Be("r0");
        var cursor = firstPage.GetProperty("nextCursor").GetString();

        var secondPage = await (await client.GetAsync($"/comments/{top}/replies?limit=2&cursor={cursor}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        secondPage.GetProperty("items").GetArrayLength().Should().Be(1); // r2 only; dead excluded
        secondPage.GetProperty("items")[0].GetProperty("body").GetString().Should().Be("r2");
    }

    [Fact]
    public async Task ListReplies_LimitOutOfRange_IsClamped()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var top = await SeedCommentAsync(clipId, userId, "top");
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
            await SeedCommentAsync(clipId, userId, $"r{i}", parentId: top, createdAt: baseTime.AddSeconds(i));
        using var client = _factory!.CreateClient();

        var low = await (await client.GetAsync($"/comments/{top}/replies?limit=0"))
            .Content.ReadFromJsonAsync<JsonElement>();
        low.GetProperty("items").GetArrayLength().Should().Be(1);
        low.GetProperty("nextCursor").GetString().Should().NotBeNullOrEmpty();

        var high = await (await client.GetAsync($"/comments/{top}/replies?limit=10000"))
            .Content.ReadFromJsonAsync<JsonElement>();
        high.GetProperty("items").GetArrayLength().Should().Be(3);
        high.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- DELETE /comments/{id} ----

    [Fact]
    public async Task Delete_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.DeleteAsync($"/comments/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_Missing_Returns404()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.DeleteAsync($"/comments/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_NonAuthor_Returns403()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAndIssueTokenAsync("owner");
        var (_, otherToken) = await SeedUserAndIssueTokenAsync("other");
        var clipId = await SeedClipAsync(ownerId);
        var commentId = await SeedCommentAsync(clipId, ownerId, "mine");
        using var client = ClientWithBearer(otherToken);

        var resp = await client.DeleteAsync($"/comments/{commentId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_Author_Returns204_AndSoftDeletes()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var commentId = await SeedCommentAsync(clipId, userId, "bye");
        using var client = ClientWithBearer(token);

        var resp = await client.DeleteAsync($"/comments/{commentId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = _fx.CreateContext();
        var row = await db.Comments.FindAsync(commentId);
        row!.DeletedAt.Should().NotBeNull();
        row.Body.Should().Be("bye"); // body retained in DB; nulled only in the API shape
    }

    [Fact]
    public async Task Delete_AlreadyDeleted_IsIdempotent()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedClipAsync(userId);
        var commentId = await SeedCommentAsync(clipId, userId, "bye", deletedAt: DateTimeOffset.UtcNow);
        using var client = ClientWithBearer(token);

        var resp = await client.DeleteAsync($"/comments/{commentId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
