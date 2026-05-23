using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Clips;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.ObjectStorage;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace GankedTV.Api.Tests.Integration.Endpoints;

[Collection("Postgres")]
public class TagsEndpointsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public TagsEndpointsTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _storage = Substitute.For<IObjectStorageService>();
        _storage.GetPresignedGetUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>())
            .Returns("https://minio.local/presigned");
        _factory = new AuthApiFactory(_fx.ConnectionString, _storage);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private async Task<Guid> SeedUserAsync(string username = "tags-test-user")
    {
        var (userId, _) = await AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);
        return userId;
    }

    private async Task<(Guid id, string shareCode)> SeedClipAsync(
        Guid userId,
        DateTimeOffset createdAt,
        string status = "ready",
        string visibility = "public",
        params string[] tagSlugs)
    {
        var id = Guid.NewGuid();
        var shareCode = ShareCodeGenerator.Next();
        await using var db = _fx.CreateContext();
        var clip = new Clip
        {
            Id = id,
            UserId = userId,
            Title = $"clip-{id:N}".Substring(0, 20),
            VideoKey = $"{userId}/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = shareCode,
            Status = status,
            Visibility = visibility,
            DurationSecs = 30,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.Clips.Add(clip);
        foreach (var slug in tagSlugs)
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Slug == slug)
                ?? new Tag { Slug = slug, Name = slug, CreatedAt = createdAt };
            if (tag.Id == 0)
            {
                db.Tags.Add(tag);
                await db.SaveChangesAsync();
            }
            db.ClipTags.Add(new ClipTag { ClipId = id, TagId = tag.Id });
        }
        await db.SaveChangesAsync();
        return (id, shareCode);
    }

    // ---- GET /tags ----

    [Fact]
    public async Task GetTags_OrdersByClipCountDescThenSlug()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, now, tagSlugs: new[] { "clutch", "ace" });
        await SeedClipAsync(userId, now, tagSlugs: new[] { "clutch" });
        await SeedClipAsync(userId, now, tagSlugs: new[] { "clutch" });
        await SeedClipAsync(userId, now, tagSlugs: new[] { "fail" });

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/tags");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var rows = arr.EnumerateArray().Select(e => new
        {
            Slug = e.GetProperty("slug").GetString(),
            Count = e.GetProperty("clipCount").GetInt32(),
        }).ToList();
        rows.Select(r => r.Slug).Should().Equal("clutch", "ace", "fail");
        rows[0].Count.Should().Be(3);
    }

    [Fact]
    public async Task GetTags_PrefixIsNormalized()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, now, tagSlugs: new[] { "clutch" });
        await SeedClipAsync(userId, now, tagSlugs: new[] { "cs2" });

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/tags?prefix=CLU");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        arr.EnumerateArray().Select(e => e.GetProperty("slug").GetString())
            .Should().ContainSingle().Which.Should().Be("clutch");
    }

    [Fact]
    public async Task GetTags_NoAuthRequired()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/tags?prefix=clu");

        resp.IsSuccessStatusCode.Should().BeTrue($"got {(int)resp.StatusCode}");
    }

    // ---- GET /tags/{slug} ----

    [Fact]
    public async Task GetBySlug_Returns200WithCount()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, now, tagSlugs: new[] { "clutch" });
        await SeedClipAsync(userId, now, tagSlugs: new[] { "clutch" });
        await SeedClipAsync(userId, now, status: "processing", tagSlugs: new[] { "clutch" });
        await SeedClipAsync(userId, now, visibility: "unlisted", tagSlugs: new[] { "clutch" });

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/tags/clutch");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("slug").GetString().Should().Be("clutch");
        body.GetProperty("clipCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task GetBySlug_Unknown_Returns404()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/tags/does-not-exist");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- GET /tags/{slug}/clips ----

    [Fact]
    public async Task GetClipsForTag_FiltersByTagAndVisibility()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        var (target1, _) = await SeedClipAsync(userId, now.AddMinutes(-1), tagSlugs: new[] { "clutch" });
        var (target2, _) = await SeedClipAsync(userId, now.AddMinutes(-2), tagSlugs: new[] { "clutch", "ace" });
        await SeedClipAsync(userId, now, tagSlugs: new[] { "fail" }); // different tag
        await SeedClipAsync(userId, now, status: "processing", tagSlugs: new[] { "clutch" });
        await SeedClipAsync(userId, now, visibility: "unlisted", tagSlugs: new[] { "clutch" });

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/tags/clutch/clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(target1, target2);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetClipsForTag_ItemsIncludeTagsArray()
    {
        await _fx.ResetAsync();
        var userId = await SeedUserAsync();
        await SeedClipAsync(userId, DateTimeOffset.UtcNow, tagSlugs: new[] { "clutch", "ace" });

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/tags/clutch/clips");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var first = body.GetProperty("items").EnumerateArray().First();
        var tags = first.GetProperty("tags").EnumerateArray()
            .Select(t => t.GetProperty("slug").GetString())
            .ToList();
        // Mapper sorts by slug.
        tags.Should().Equal("ace", "clutch");
    }

    [Fact]
    public async Task GetClipsForTag_UnknownSlug_Returns404()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/tags/does-not-exist/clips");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
