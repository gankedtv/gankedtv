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

// Covers the tag-related behaviour of POST /clips and PATCH /clips/{id}. Kept
// separate from ClipsUploadEndpointsTests / ClipsMutateEndpointsTests so the tag
// feature lives in one file and is easy to delete or move later.
[Collection("Postgres")]
public class ClipsTagsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private AuthApiFactory? _factory;
    private IObjectStorageService _storage = null!;

    public ClipsTagsTests(PostgresFixture fx) => _fx = fx;

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

    private Task<(Guid userId, string token)> SeedUserAndIssueTokenAsync(string username = "owner") =>
        AuthTestHelpers.SeedUserAndIssueTokenAsync(_fx, _factory!, username);

    private HttpClient ClientWithBearer(string token) =>
        AuthTestHelpers.CreateBearerClient(_factory!, token);

    private async Task<Guid> SeedReadyClipAsync(Guid userId, params string[] tagSlugs)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.CreateContext();
        db.Clips.Add(new Clip
        {
            Id = id,
            UserId = userId,
            Title = "seed",
            VideoKey = $"{userId}/{id}.mp4",
            ThumbnailKey = $"thumbs/{id}.jpg",
            ShareCode = ShareCodeGenerator.Next(),
            Status = "ready",
            Visibility = "public",
            CreatedAt = now,
            UpdatedAt = now,
        });
        foreach (var slug in tagSlugs)
        {
            var tag = new Tag { Slug = slug, Name = slug, CreatedAt = now };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            db.ClipTags.Add(new ClipTag { ClipId = id, TagId = tag.Id });
        }
        await db.SaveChangesAsync();
        return id;
    }

    // ---- POST /clips with tags ----

    [Fact]
    public async Task Create_WithTags_PersistsClipTagsRows()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new
        {
            title = "clutch ace",
            tags = new[] { "clutch", "ace" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var clipId = body.GetProperty("id").GetGuid();

        await using var db = _fx.CreateContext();
        var slugs = await db.ClipTags
            .Where(ct => ct.ClipId == clipId)
            .Select(ct => ct.Tag.Slug)
            .ToListAsync();
        slugs.Should().BeEquivalentTo(new[] { "clutch", "ace" });
    }

    [Fact]
    public async Task Create_TagsNormalizeAndDedupeWithinRequest()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new
        {
            title = "x",
            tags = new[] { "Clutch", "clutch", "CLUTCH" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var clipId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var db = _fx.CreateContext();
        var tags = await db.ClipTags.Where(ct => ct.ClipId == clipId)
            .Select(ct => ct.Tag.Slug).ToListAsync();
        tags.Should().ContainSingle().Which.Should().Be("clutch");

        var totalTagRows = await db.Tags.CountAsync(t => t.Slug == "clutch");
        totalTagRows.Should().Be(1);
    }

    [Fact]
    public async Task Create_SixDistinctTags_Returns400TooManyTags()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new
        {
            title = "x",
            tags = new[] { "a1", "a2", "a3", "a4", "a5", "a6" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("too_many_tags");
    }

    [Fact]
    public async Task Create_InvalidTag_Returns400InvalidTag()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var resp = await client.PostAsJsonAsync("/clips", new
        {
            title = "x",
            tags = new[] { "a" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_tag");
    }

    [Fact]
    public async Task Create_ReusesExistingTagRow()
    {
        await _fx.ResetAsync();
        var (_, token) = await SeedUserAndIssueTokenAsync();
        using var client = ClientWithBearer(token);

        var first = await client.PostAsJsonAsync("/clips", new { title = "x", tags = new[] { "clutch" } });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await client.PostAsJsonAsync("/clips", new { title = "y", tags = new[] { "Clutch" } });
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        var rows = await db.Tags.Where(t => t.Slug == "clutch").ToListAsync();
        rows.Should().HaveCount(1);
    }

    // ---- PATCH /clips/{id} with tags ----

    [Fact]
    public async Task Patch_TagsOmitted_LeavesExistingSetUnchanged()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedReadyClipAsync(userId, "clutch", "ace");
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "renamed" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        var slugs = await db.ClipTags.Where(ct => ct.ClipId == clipId)
            .Select(ct => ct.Tag.Slug).ToListAsync();
        slugs.Should().BeEquivalentTo(new[] { "clutch", "ace" });
    }

    [Fact]
    public async Task Patch_EmptyTagsArray_ClearsAll()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedReadyClipAsync(userId, "clutch", "ace");
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { tags = Array.Empty<string>() });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _fx.CreateContext();
        var count = await db.ClipTags.CountAsync(ct => ct.ClipId == clipId);
        count.Should().Be(0);
    }

    [Fact]
    public async Task Patch_ReplaceTags_DiffsAddAndRemove()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedReadyClipAsync(userId, "clutch", "ace");
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new
        {
            tags = new[] { "clutch", "fail" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var responseSlugs = body.GetProperty("tags").EnumerateArray()
            .Select(t => t.GetProperty("slug").GetString()).ToList();
        responseSlugs.Should().Equal("clutch", "fail");

        await using var db = _fx.CreateContext();
        var slugs = await db.ClipTags.Where(ct => ct.ClipId == clipId)
            .Select(ct => ct.Tag.Slug).ToListAsync();
        slugs.Should().BeEquivalentTo(new[] { "clutch", "fail" });
    }

    [Fact]
    public async Task Patch_TooManyTags_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedReadyClipAsync(userId);
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new
        {
            tags = new[] { "a1", "a2", "a3", "a4", "a5", "a6" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("too_many_tags");
    }

    [Fact]
    public async Task Patch_ResubmitSameTagSet_IsIdempotent_NoChurn()
    {
        // Guards against a future change to SetClipTags that would naively delete-then-reinsert
        // the same rows on every PATCH. We pin behaviour by capturing the existing rows' identities
        // (composite PK (ClipId, TagId)) before + after and asserting they're unchanged.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedReadyClipAsync(userId, "clutch", "ace");
        using var client = ClientWithBearer(token);

        int[] tagIdsBefore;
        await using (var db = _fx.CreateContext())
        {
            tagIdsBefore = await db.ClipTags.Where(ct => ct.ClipId == clipId)
                .OrderBy(ct => ct.TagId).Select(ct => ct.TagId).ToArrayAsync();
        }

        // Submit the same set in a different order — server normalizes anyway.
        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new
        {
            tags = new[] { "ace", "Clutch" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        var tagIdsAfter = await verify.ClipTags.Where(ct => ct.ClipId == clipId)
            .OrderBy(ct => ct.TagId).Select(ct => ct.TagId).ToArrayAsync();
        tagIdsAfter.Should().Equal(tagIdsBefore);
    }

    [Fact]
    public async Task Patch_TagsOmitted_DoesNotTouchClipTagsRows()
    {
        // Tighter version of Patch_TagsOmitted_LeavesExistingSetUnchanged: also asserts the
        // resolver wasn't invoked on the tags collection (no delete-then-reinsert churn) by
        // pinning the row identities — a regression that always called the resolver with
        // (req.Tags ?? []) would clear the set and this would fail.
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedReadyClipAsync(userId, "clutch", "ace");
        using var client = ClientWithBearer(token);

        int[] tagIdsBefore;
        await using (var db = _fx.CreateContext())
        {
            tagIdsBefore = await db.ClipTags.Where(ct => ct.ClipId == clipId)
                .OrderBy(ct => ct.TagId).Select(ct => ct.TagId).ToArrayAsync();
        }

        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new { title = "renamed" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = _fx.CreateContext();
        var tagIdsAfter = await verify.ClipTags.Where(ct => ct.ClipId == clipId)
            .OrderBy(ct => ct.TagId).Select(ct => ct.TagId).ToArrayAsync();
        tagIdsAfter.Should().Equal(tagIdsBefore);
    }

    [Fact]
    public async Task Patch_InvalidTag_Returns400()
    {
        await _fx.ResetAsync();
        var (userId, token) = await SeedUserAndIssueTokenAsync();
        var clipId = await SeedReadyClipAsync(userId);
        using var client = ClientWithBearer(token);

        var resp = await client.PatchAsJsonAsync($"/clips/{clipId}", new
        {
            tags = new[] { "!!!" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("invalid_tag");
    }
}
