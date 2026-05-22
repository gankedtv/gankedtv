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

[Collection("Postgres")]
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
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = "public",
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
    public async Task Create_TopLevel_Returns201WithItem()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(userId);
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/comments", new { body = "  first!  " });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await resp.Content.ReadFromJsonAsync<JsonElement>();
        item.GetProperty("body").GetString().Should().Be("first!"); // trimmed
        item.GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("replyCount").GetInt32().Should().Be(0);
        item.GetProperty("deleted").GetBoolean().Should().BeFalse();
        item.GetProperty("author").GetProperty("username").GetString().Should().Be("author");
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
