using FluentAssertions;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Services.Tags;
using GankedTV.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Tests.Services.Tags;

[Collection("PostgresDiscovery")]
public class TagsResolverTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;

    public TagsResolverTests(PostgresFixture fx) => _fx = fx;

    public async Task InitializeAsync() => await _fx.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TagsResolver CreateResolver(out GankedTvDbContext db)
    {
        db = _fx.CreateContext();
        return new TagsResolver(db, TimeProvider.System);
    }

    [Fact]
    public async Task ResolveAsync_EmptyList_ReturnsEmpty()
    {
        var resolver = CreateResolver(out var db);
        await using var _ = db;

        var result = await resolver.ResolveAsync([], default);

        result.IsSuccess.Should().BeTrue();
        result.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_CreatesNewTagsAndReturnsThem()
    {
        var resolver = CreateResolver(out var db);
        await using var _ = db;

        var result = await resolver.ResolveAsync(["clutch", "ace"], default);

        result.IsSuccess.Should().BeTrue();
        result.Tags.Should().HaveCount(2);
        result.Tags.Select(t => t.Slug).Should().Equal("clutch", "ace");

        var stored = await db.Tags.ToListAsync();
        stored.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolveAsync_ReusesExistingTagsBySlug()
    {
        // Pre-seed "clutch" via one resolver instance.
        {
            var resolver = CreateResolver(out var db);
            await using (db)
            {
                await resolver.ResolveAsync(["clutch"], default);
            }
        }

        var resolver2 = CreateResolver(out var db2);
        await using (db2)
        {
            var result = await resolver2.ResolveAsync(["Clutch", "ace"], default);
            result.IsSuccess.Should().BeTrue();
            result.Tags.Select(t => t.Slug).Should().Equal("clutch", "ace");
        }

        // No duplicate clutch row.
        await using var verify = _fx.CreateContext();
        var stored = await verify.Tags.Where(t => t.Slug == "clutch").ToListAsync();
        stored.Should().HaveCount(1);
    }

    [Fact]
    public async Task ResolveAsync_NormalizesAndDedupesWithinRequest()
    {
        var resolver = CreateResolver(out var db);
        await using var _ = db;

        var result = await resolver.ResolveAsync(["Clutch", "clutch", "CLUTCH", "ace"], default);

        result.IsSuccess.Should().BeTrue();
        result.Tags.Select(t => t.Slug).Should().Equal("clutch", "ace");

        var stored = await db.Tags.ToListAsync();
        stored.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolveAsync_SixDistinctTags_ReturnsTooManyTags()
    {
        var resolver = CreateResolver(out var db);
        await using var _ = db;

        var result = await resolver.ResolveAsync(["a1", "a2", "a3", "a4", "a5", "a6"], default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TagsResolveError.TooManyTags);
    }

    [Fact]
    public async Task ResolveAsync_SixDuplicates_IsAcceptedAfterDedupe()
    {
        var resolver = CreateResolver(out var db);
        await using var _ = db;

        // After dedupe this is one tag, not six.
        var result = await resolver.ResolveAsync(["clutch", "clutch", "clutch", "clutch", "clutch", "clutch"], default);

        result.IsSuccess.Should().BeTrue();
        result.Tags.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("!!!")]
    public async Task ResolveAsync_InvalidTag_ReturnsInvalidTag(string raw)
    {
        var resolver = CreateResolver(out var db);
        await using var _ = db;

        var result = await resolver.ResolveAsync([raw], default);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TagsResolveError.InvalidTag);
    }

    [Fact]
    public async Task ResolveAsync_WhitespaceInRaw_NormalizesToHyphenSlug()
    {
        var resolver = CreateResolver(out var db);
        await using var _ = db;

        var result = await resolver.ResolveAsync(["With Space"], default);

        result.IsSuccess.Should().BeTrue();
        result.Tags.Single().Slug.Should().Be("with-space");
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentInsertVisibleToFreshResolver_NoRowChurn()
    {
        // Adjacent-test for the unique-violation retry path: if a sibling context inserted
        // "clutch" before our resolver runs its SELECT, the resolver should pick up the
        // existing row (no INSERT attempted, no conflict). Pins the happy concurrent case.
        // The true SELECT-then-conflict-then-retry path can't be driven deterministically
        // from a single xUnit test thread (DbContext isn't thread-safe and we can't pause
        // SaveChanges mid-flight), but the retry code is small and self-contained.
        await using (var sibling = _fx.CreateContext())
        {
            sibling.Tags.Add(new Tag { Slug = "clutch", Name = "clutch", CreatedAt = DateTimeOffset.UtcNow });
            await sibling.SaveChangesAsync();
        }

        var resolver = CreateResolver(out var db);
        await using var _ = db;

        var result = await resolver.ResolveAsync(["Clutch"], default);
        result.IsSuccess.Should().BeTrue();
        result.Tags.Single().Slug.Should().Be("clutch");

        await using var verify = _fx.CreateContext();
        (await verify.Tags.CountAsync(t => t.Slug == "clutch")).Should().Be(1);
    }

    [Fact]
    public void SetClipTags_DiffsAddsAndRemoves()
    {
        var resolver = CreateResolver(out var db);
        using var _ = db;

        var clip = new Clip
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Title = "x",
            VideoKey = "k",
            ShareCode = "abcabc",
        };
        var existingA = new Tag { Id = 100, Slug = "a", Name = "a" };
        var existingB = new Tag { Id = 200, Slug = "b", Name = "b" };
        clip.ClipTags.Add(new ClipTag { ClipId = clip.Id, TagId = existingA.Id, Tag = existingA });
        clip.ClipTags.Add(new ClipTag { ClipId = clip.Id, TagId = existingB.Id, Tag = existingB });

        var newC = new Tag { Id = 300, Slug = "c", Name = "c" };
        resolver.SetClipTags(clip, [existingA, newC]);

        // existingB is removed, newC added, existingA kept.
        clip.ClipTags.Select(ct => ct.TagId).Should().BeEquivalentTo(new[] { 100, 300 });
    }
}
