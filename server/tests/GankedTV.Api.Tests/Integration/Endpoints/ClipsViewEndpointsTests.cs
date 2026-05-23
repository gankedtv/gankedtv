using System.Net;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class ClipsViewEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ClipsViewEndpointsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "viewer") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<Guid> SeedClipAsync(
        Guid userId,
        string status = "ready",
        string visibility = "public",
        int initialViewCount = 0)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = "viewable",
            VideoKey = $"clips/{userId}/{id}.mp4",
            ShareCode = ShareCodeGenerator.Next(),
            Status = status,
            Visibility = visibility,
            ViewCount = initialViewCount,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task RecordView_Anonymous_Returns204AndIncrementsCounter()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(authorId);

        using var client = _factory!.CreateClient();
        var resp = await client.PostAsync($"/clips/{clipId}/view", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = _fx.CreateContext();
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.ViewCount).FirstAsync()).Should().Be(1);
        (await db.ClipViews.CountAsync(v => v.ClipId == clipId)).Should().Be(1);
    }

    [Fact]
    public async Task RecordView_RepeatedFromSameIp_IsDedupedToOne()
    {
        // 30-min dedup window must collapse a sustained-watch session into one counted view.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var clipId = await SeedClipAsync(authorId);

        using var client = _factory!.CreateClient();
        var first = await client.PostAsync($"/clips/{clipId}/view", content: null);
        var second = await client.PostAsync($"/clips/{clipId}/view", content: null);
        var third = await client.PostAsync($"/clips/{clipId}/view", content: null);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        third.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var db = _fx.CreateContext();
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.ViewCount).FirstAsync()).Should().Be(1);
        (await db.ClipViews.CountAsync(v => v.ClipId == clipId)).Should().Be(1);
    }

    [Fact]
    public async Task RecordView_NonExistentClip_Returns204Silently()
    {
        // Spec: "Returns 204 on success and on duplicate (silently no-op)." A missing clip is
        // indistinguishable from an unlisted/processing clip from the client's perspective.
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.PostAsync($"/clips/{Guid.NewGuid()}/view", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var db = _fx.CreateContext();
        (await db.ClipViews.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RecordView_NonPublicClip_Returns204AndNoIncrement()
    {
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var unlistedClip = await SeedClipAsync(authorId, visibility: "unlisted");
        var processingClip = await SeedClipAsync(authorId, status: "processing");

        using var client = _factory!.CreateClient();
        var unlistedResp = await client.PostAsync($"/clips/{unlistedClip}/view", content: null);
        var processingResp = await client.PostAsync($"/clips/{processingClip}/view", content: null);

        unlistedResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        processingResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var db = _fx.CreateContext();
        (await db.Clips.Where(c => c.Id == unlistedClip).Select(c => c.ViewCount).FirstAsync()).Should().Be(0);
        (await db.Clips.Where(c => c.Id == processingClip).Select(c => c.ViewCount).FirstAsync()).Should().Be(0);
        (await db.ClipViews.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RecordView_AuthenticatedDedupsByUserNotIp()
    {
        // Two different authed callers sharing one test-rig IP must both count: dedup keys on
        // the JWT sub for authed callers, so shared NAT or office wifi doesn't collapse
        // distinct viewers into one.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (_, viewerToken1) = await SeedUserAndIssueTokenAsync("v1");
        var (_, viewerToken2) = await SeedUserAndIssueTokenAsync("v2");
        var clipId = await SeedClipAsync(authorId);

        using (var c1 = ClientWithBearer(viewerToken1))
        {
            (await c1.PostAsync($"/clips/{clipId}/view", content: null))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using (var c2 = ClientWithBearer(viewerToken2))
        {
            (await c2.PostAsync($"/clips/{clipId}/view", content: null))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        await using var db = _fx.CreateContext();
        (await db.Clips.Where(c => c.Id == clipId).Select(c => c.ViewCount).FirstAsync()).Should().Be(2);
        (await db.ClipViews.CountAsync(v => v.ClipId == clipId)).Should().Be(2);
    }

    [Fact]
    public async Task RecordView_ExceedingRateLimit_Returns429()
    {
        // 20/min per IP. Fire 21 POSTs against distinct clip ids (so the cache dedup doesn't
        // kick in and mask the limiter) and the last one must hit the limit.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var clipIds = new List<Guid>();
        for (var i = 0; i < 22; i++)
        {
            clipIds.Add(await SeedClipAsync(authorId));
        }

        using var client = _factory!.CreateClient();
        var statuses = new List<HttpStatusCode>();
        foreach (var id in clipIds)
        {
            var resp = await client.PostAsync($"/clips/{id}/view", content: null);
            statuses.Add(resp.StatusCode);
        }

        statuses.Take(ClipsRateLimiting.ViewPermitLimit).Should()
            .OnlyContain(s => s == HttpStatusCode.NoContent);
        statuses.Skip(ClipsRateLimiting.ViewPermitLimit).Should()
            .OnlyContain(s => s == HttpStatusCode.TooManyRequests);
    }
}
