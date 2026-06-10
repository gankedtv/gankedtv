using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("PostgresAdmin")]
public class ReportsEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ReportsEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAsync(string username, string role = UserRoles.User) =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username, u => u.Role = role);

    private async Task<Guid> SeedClipAsync(Guid ownerId, string visibility = "public", string status = "ready")
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = ownerId,
            Title = "target",
            VideoKey = $"clips/{ownerId}/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = ShareCodeGenerator.Next(),
            Status = status,
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedCommentAsync(Guid clipId, Guid authorId)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Comments.Add(new Comment
        {
            Id = id,
            ClipId = clipId,
            UserId = authorId,
            Body = "to-report",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ReportClip_NoBearer_Returns401()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();
        var resp = await client.PostAsJsonAsync($"/clips/{Guid.NewGuid()}/report", new { reason = "spam" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReportClip_HappyPath_Creates201AndPersistsRow()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (_, reporterToken) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/report",
            new { reason = "spam", note = "looks shady" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var verify = _fx.CreateContext();
        (await verify.Reports.CountAsync(r => r.TargetId == clipId)).Should().Be(1);
    }

    [Fact]
    public async Task ReportClip_SelfReport_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, ownerToken) = await SeedUserAsync("owner");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, ownerToken);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/report", new { reason = "spam" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReportClip_DuplicateOpen_Returns409()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (_, reporterToken) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var first = await client.PostAsJsonAsync($"/clips/{clipId}/report", new { reason = "spam" });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync($"/clips/{clipId}/report", new { reason = "harassment" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReportClip_UnknownTarget_Returns404()
    {
        await _fx.ResetAsync();
        var (_, reporterToken) = await SeedUserAsync("reporter");
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);

        var resp = await client.PostAsJsonAsync($"/clips/{Guid.NewGuid()}/report", new { reason = "spam" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReportClip_OtherWithoutNote_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (_, reporterToken) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/report",
            new { reason = "other" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReportClip_InvalidReason_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (_, reporterToken) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var resp = await client.PostAsJsonAsync($"/clips/{clipId}/report", new { reason = "bogus" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReportComment_SelfReport_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (authorId, authorToken) = await SeedUserAsync("author");
        var clipId = await SeedClipAsync(ownerId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, authorToken);
        var resp = await client.PostAsJsonAsync($"/comments/{commentId}/report", new { reason = "spam" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReportComment_HappyPath_Creates201()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (authorId, _) = await SeedUserAsync("author");
        var (_, reporterToken) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var resp = await client.PostAsJsonAsync($"/comments/{commentId}/report",
            new { reason = "harassment" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ReportUser_SelfReport_Returns400()
    {
        await _fx.ResetAsync();
        var (selfId, selfToken) = await SeedUserAsync("self");

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, selfToken);
        var resp = await client.PostAsJsonAsync($"/users/{selfId}/report", new { reason = "spam" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReportUser_HappyPath_Creates201()
    {
        await _fx.ResetAsync();
        var (targetId, _) = await SeedUserAsync("target");
        var (_, reporterToken) = await SeedUserAsync("reporter");

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var resp = await client.PostAsJsonAsync($"/users/{targetId}/report",
            new { reason = "harassment" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- Parity coverage: comment + user variants exercise the same MapError branches as
    // the clip variant. Each MapPost lambda is its own compiled handler, so the per-arm
    // branches need a hit per variant to land in coverage.

    [Fact]
    public async Task ReportComment_UnknownTarget_Returns404()
    {
        await _fx.ResetAsync();
        var (_, reporterToken) = await SeedUserAsync("reporter");
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);

        var resp = await client.PostAsJsonAsync($"/comments/{Guid.NewGuid()}/report",
            new { reason = "spam" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReportComment_InvalidReason_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (authorId, _) = await SeedUserAsync("author");
        var (_, reporterToken) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var resp = await client.PostAsJsonAsync($"/comments/{commentId}/report",
            new { reason = "bogus" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReportUser_UnknownTarget_Returns404()
    {
        await _fx.ResetAsync();
        var (_, reporterToken) = await SeedUserAsync("reporter");
        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);

        var resp = await client.PostAsJsonAsync($"/users/{Guid.NewGuid()}/report",
            new { reason = "harassment" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReportUser_DuplicateOpen_Returns409()
    {
        await _fx.ResetAsync();
        var (targetId, _) = await SeedUserAsync("target");
        var (_, reporterToken) = await SeedUserAsync("reporter");

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var first = await client.PostAsJsonAsync($"/users/{targetId}/report",
            new { reason = "harassment" });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync($"/users/{targetId}/report",
            new { reason = "hate" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReportComment_OtherWithoutNote_Returns400()
    {
        await _fx.ResetAsync();
        var (ownerId, _) = await SeedUserAsync("owner");
        var (authorId, _) = await SeedUserAsync("author");
        var (_, reporterToken) = await SeedUserAsync("reporter");
        var clipId = await SeedClipAsync(ownerId);
        var commentId = await SeedCommentAsync(clipId, authorId);

        using var client = AuthTestHelpers.CreateBearerClient(_factory!, reporterToken);
        var resp = await client.PostAsJsonAsync($"/comments/{commentId}/report",
            new { reason = "other" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
