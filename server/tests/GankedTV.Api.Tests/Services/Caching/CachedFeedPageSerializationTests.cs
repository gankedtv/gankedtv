using System.Text.Json;
using FluentAssertions;
using GankedTV.Api.Contracts.Clips;
using GankedTV.Api.Contracts.Games;
using GankedTV.Api.Contracts.Tags;
using GankedTV.Api.Services.Caching;
using Xunit;

namespace GankedTV.Api.Tests.Services.Caching;

// The unit/integration tests run HybridCache L1-only, where values are stored by reference and
// never serialized — so a CachedFeedPage that fails to round-trip through the Redis L2 would only
// surface in production. HybridCache serializes objects with System.Text.Json by default, so this
// asserts the cached payload (a positional record with nested records + a collection) survives a
// default STJ round-trip — the failure mode being a type STJ can't reconstruct.
public class CachedFeedPageSerializationTests
{
    [Fact]
    public void CachedFeedPage_RoundTripsThroughSystemTextJson()
    {
        var page = new CachedFeedPage(
            [
                new ClipFeedItem(
                    Id: Guid.NewGuid(),
                    ShareCode: "abc123",
                    Title: "Clutch 1v5",
                    Description: "ace on Ascent",
                    ThumbnailUrl: "https://cdn.example/thumb.jpg?sig=xyz",
                    DurationSecs: 42,
                    ViewCount: 1234,
                    LikeCount: 56,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Author: new AuthorSummary(Guid.NewGuid(), "player1", "https://cdn.example/avatar.png"),
                    Game: new GameSummary(7, "Valorant", "valorant", "VAL"),
                    Tags: [new TagSummary(1, "ace", "Ace", 99)],
                    LikedByMe: false),
                // A second item with the nullable fields null, to cover those branches too.
                new ClipFeedItem(
                    Id: Guid.NewGuid(),
                    ShareCode: "def456",
                    Title: "No game tag",
                    Description: null,
                    ThumbnailUrl: "https://cdn.example/thumb2.jpg",
                    DurationSecs: null,
                    ViewCount: 0,
                    LikeCount: 0,
                    CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                    Author: new AuthorSummary(Guid.NewGuid(), "player2", null),
                    Game: null,
                    Tags: [],
                    LikedByMe: false),
            ],
            NextCursor: "cursor-token");

        var json = JsonSerializer.Serialize(page);
        var roundTripped = JsonSerializer.Deserialize<CachedFeedPage>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.NextCursor.Should().Be("cursor-token");
        roundTripped.Items.Should().BeEquivalentTo(page.Items); // every nested field must survive
    }
}
