# For You personalized feed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a personalized `source=for-you` feed that re-orders the public+ready feed into relevance tiers (followed authors → liked games → global backfill), newest-first within each tier, with true keyset pagination and a transparent anonymous/cold-start fallback to Latest.

**Architecture:** A new `ForYouFeedBuilder` owns tier construction, cross-tier page fill, and a tiered cursor; it returns feed-hydrated `Clip`s + a next-cursor. A new `TieredKeysetCursor` extends the opaque keyset token to a `(tier, createdAt, id)` triple. `ClipsReadEndpoints.GetFeed` gains an additive `isForYou` branch that calls the builder for signed-in users with signals and otherwise falls through to the existing latest path. Web widens the feed source union and makes For You the default Home tab.

**Tech Stack:** C# / .NET 10, EF Core (PostgreSQL), xUnit + FluentAssertions + Testcontainers; Vue 3 + TypeScript + Vitest.

## Global Constraints

- **Server coverage gate: 85% line / 85% branch** (total) — enforced by CI and pre-push hook.
- **Web coverage gate: 85% line / 85% branch**, scoped to `src/api/**`, `src/router/**`, `src/stores/**` (views excluded, but their existing tests must still pass).
- **Backward-compatible & additive only**: `source` matching stays lenient/case-insensitive; no changes to the trending, following, or latest code paths' behavior.
- **No new design tokens or layout** — three tabs fit the existing `UnderlineTabs`. Re-read `web/DESIGN.md` before touching any Vue template.
- **No AI attribution** in commits or PRs; **no issue/PR numbers in source comments** (prose docs may cross-reference).
- Comments only where the *why* is non-obvious; one or two tight lines.
- Order within the feed is always `tier ASC, createdAt DESC, id DESC`.
- Seeded game rows available in the test template DB: `Id=1..9` (2=Valorant/`valorant`/`VALORANT`, 3=CS2/`cs2`/`CS2`). Tests reference these IDs directly.

---

### Task 1: `TieredKeysetCursor` — `(tier, createdAt, id)` opaque token

**Files:**
- Create: `server/src/GankedTV.Api/Pagination/TieredKeysetCursor.cs`
- Test: `server/tests/GankedTV.Api.Tests/Data/TieredKeysetCursorTests.cs`

**Interfaces:**
- Consumes: nothing (mirrors the existing `KeysetCursor` scheme in the same namespace).
- Produces:
  - `string TieredKeysetCursor.Build(int tier, DateTimeOffset createdAt, Guid id)`
  - `bool TieredKeysetCursor.TryParse(string? raw, out int tier, out DateTimeOffset createdAt, out Guid id)` — `false` on null/empty/corrupt, and on `false` sets `tier=0` (silent tier-0 restart). Task 2 depends on both.

- [ ] **Step 1: Write the failing unit tests**

Create `server/tests/GankedTV.Api.Tests/Data/TieredKeysetCursorTests.cs`:

```csharp
using FluentAssertions;
using GankedTV.Api.Pagination;

namespace GankedTV.Api.Tests.Data;

public class TieredKeysetCursorTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 4, 18, 23, 31, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_Then_TryParse_RoundTrips(int tier)
    {
        var token = TieredKeysetCursor.Build(tier, T, Id);

        var ok = TieredKeysetCursor.TryParse(token, out var parsedTier, out var parsedCreatedAt, out var parsedId);

        ok.Should().BeTrue();
        parsedTier.Should().Be(tier);
        parsedCreatedAt.Should().Be(T);
        parsedId.Should().Be(Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!not-base64!!!")]
    [InlineData("YWJj")] // valid Base64Url decoding to "abc" — no separators, wrong structure
    public void TryParse_NullEmptyOrCorrupt_ReturnsFalse_AndTierZero(string? raw)
    {
        var ok = TieredKeysetCursor.TryParse(raw, out var tier, out _, out _);

        ok.Should().BeFalse();
        tier.Should().Be(0);
    }

    [Fact]
    public void TryParse_PlainKeysetCursorToken_ReturnsFalse()
    {
        // A two-part (createdAt, id) token from Latest/Following must not parse as tiered —
        // it lacks the leading tier segment, so For You restarts from tier 0.
        var plain = KeysetCursor.Build(T, Id);

        var ok = TieredKeysetCursor.TryParse(plain, out var tier, out _, out _);

        ok.Should().BeFalse();
        tier.Should().Be(0);
    }

    [Fact]
    public void KeysetCursor_TryParse_RejectsTieredToken()
    {
        // Cross-source safety the other way: a tiered token fed to the Latest/Following decoder
        // fails (the id segment can't parse), so it falls back to no-cursor.
        var tiered = TieredKeysetCursor.Build(1, T, Id);

        KeysetCursor.TryParse(tiered, out _, out _).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test server --filter "FullyQualifiedName~TieredKeysetCursorTests"`
Expected: FAIL to compile — `TieredKeysetCursor` does not exist.

- [ ] **Step 3: Implement `TieredKeysetCursor`**

Create `server/src/GankedTV.Api/Pagination/TieredKeysetCursor.cs`:

```csharp
using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace GankedTV.Api.Pagination;

/// <summary>
/// Extends the opaque keyset token to a <c>(tier, createdAt, id)</c> triple for the tiered
/// For You feed. Payload <c>{tier}_{createdAt:O}_{id:D}</c>, Base64Url-encoded (same scheme as
/// <see cref="KeysetCursor"/>). Neither the <c>O</c> date format nor a <c>D</c>-format Guid
/// contains <c>_</c>, so <c>Split('_', 3)</c> decodes unambiguously.
/// </summary>
public static class TieredKeysetCursor
{
    private const char Separator = '_';

    public static string Build(int tier, DateTimeOffset createdAt, Guid id)
    {
        var payload =
            $"{tier.ToString(CultureInfo.InvariantCulture)}{Separator}" +
            $"{createdAt.ToString("O", CultureInfo.InvariantCulture)}{Separator}{id:D}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Parses a tiered cursor. Returns <c>false</c> on null/empty/corrupt input (including a
    /// plain <see cref="KeysetCursor"/> token, which lacks the leading tier segment), leaving
    /// <paramref name="tier"/> at 0 so callers silently restart from tier 0.
    /// </summary>
    public static bool TryParse(string? raw, out int tier, out DateTimeOffset createdAt, out Guid id)
    {
        tier = 0;
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(raw);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var parts = decoded.Split(Separator, 3);
        if (parts.Length != 3) return false;

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out tier)
            && DateTimeOffset.TryParse(
                parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt)
            && Guid.TryParse(parts[2], out id);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test server --filter "FullyQualifiedName~TieredKeysetCursorTests"`
Expected: PASS (all theory cases + facts).

- [ ] **Step 5: Commit**

```bash
git add server/src/GankedTV.Api/Pagination/TieredKeysetCursor.cs \
        server/tests/GankedTV.Api.Tests/Data/TieredKeysetCursorTests.cs
git commit -m "feat(feed): add TieredKeysetCursor for the For You feed"
```

---

### Task 2: `ForYouFeedBuilder` + endpoint wiring (driven by integration tests)

The builder's tier-fill and cursor logic are interdependent, so this task delivers the complete builder plus the `GetFeed` branch together, driven by the full integration-test suite. Task 1's cursor is a hard dependency.

**Files:**
- Create: `server/src/GankedTV.Api/Services/Feeds/ForYouFeedBuilder.cs`
- Modify: `server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs` (add the `isForYou` branch in `GetFeed`, after the trending block, before the cached-latest block — currently between lines 194 and 196)
- Test: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs` (append a new `// ---- GET /clips/feed?source=for-you ----` region)

**Interfaces:**
- Consumes: `TieredKeysetCursor.Build/TryParse` (Task 1); `KeysetPagination.WhereKeysetBefore` (`server/src/GankedTV.Api/Pagination/KeysetPagination.cs`); `ClipQueryExtensions.WherePublicReady`/`IncludeFeedRelations` (`server/src/GankedTV.Api/Data/ClipQueryExtensions.cs`); `ClipsReadEndpoints.FeedDefaultLimit`/`FeedMaxLimit` (internal consts, same assembly); `ClipsReadEndpoints.ProjectFeedItemsAsync` + `ClipFeedResponse` (call site).
- Produces:
  - `internal sealed record ForYouPage(IReadOnlyList<Clip> Clips, string? NextCursor)`
  - `internal static Task<ForYouPage?> ForYouFeedBuilder.BuildPageAsync(GankedTvDbContext db, Guid me, string? cursor, int? limit, CancellationToken ct)` — returns `null` for a signal-less caller (cold-start).

- [ ] **Step 1: Write the failing integration tests**

Append this region to `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs`, immediately before the `// ---- GET /clips/{id} ----` region (~line 772). It reuses the existing private helpers `SeedUserAndIssueTokenAsync`, `SeedClipAsync`, `ClientWithBearer`, and `_fx`/`_factory`.

```csharp
    // ---- GET /clips/feed?source=for-you ----

    // me follows `followed`; me has liked a clip in game 2 (Valorant). Returns the ids of the
    // three seeded authors plus me's token/client so each test can assert tier placement.
    private async Task<(Guid me, string token, Guid followed, Guid stranger)> SeedForYouSignalsAsync()
    {
        var (me, token) = await SeedUserAndIssueTokenAsync("reader");
        var (followed, _) = await SeedUserAndIssueTokenAsync("followed");
        var (stranger, _) = await SeedUserAndIssueTokenAsync("stranger");

        // Establish liked-game {2}: me likes an (unlisted, so it never appears in the feed
        // itself) clip in game 2. The liked-game signal ignores the liked clip's visibility.
        var (likeSeed, _) = await SeedClipAsync(
            stranger, DateTimeOffset.UtcNow, visibility: "unlisted", title: "like-seed", gameId: 2);
        await using (var db = _fx.CreateContext())
        {
            db.Follows.Add(new Follow { FollowerId = me, FolloweeId = followed, CreatedAt = DateTimeOffset.UtcNow });
            db.Likes.Add(new Like { UserId = me, ClipId = likeSeed, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        return (me, token, followed, stranger);
    }

    private static List<Guid> FeedIds(JsonElement body) =>
        body.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task ForYou_Anonymous_IdenticalToLatest()
    {
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(userId, now.AddMinutes(-1), title: "c1");
        await SeedClipAsync(userId, now.AddMinutes(-2), title: "c2");
        await SeedClipAsync(userId, now.AddMinutes(-3), title: "c3");

        using var client = _factory!.CreateClient();
        var forYou = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");
        var latest = await client.GetFromJsonAsync<JsonElement>("/clips/feed");

        FeedIds(forYou).Should().Equal(FeedIds(latest));
    }

    [Fact]
    public async Task ForYou_AuthenticatedColdStart_IdenticalToLatest()
    {
        await _fx.ResetAsync();
        var (me, token) = await SeedUserAndIssueTokenAsync("reader");
        var (author, _) = await SeedUserAndIssueTokenAsync("author");
        var now = DateTimeOffset.UtcNow;
        await SeedClipAsync(author, now.AddMinutes(-1), title: "c1");
        await SeedClipAsync(author, now.AddMinutes(-2), title: "c2");

        using var client = ClientWithBearer(token);
        var forYou = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");
        var latest = await client.GetFromJsonAsync<JsonElement>("/clips/feed");

        FeedIds(forYou).Should().Equal(FeedIds(latest));
    }

    [Fact]
    public async Task ForYou_TierOrdering_FollowBeatsLikedGameBeatsBackfill_RegardlessOfRecency()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        // Tier 0 is the OLDEST clip, tier 2 the NEWEST — proves tier dominates recency.
        var (t0, _) = await SeedClipAsync(followed, now.AddMinutes(-10), title: "t0-follow");
        var (t1, _) = await SeedClipAsync(stranger, now.AddMinutes(-5), title: "t1-liked-game", gameId: 2);
        var (t2, _) = await SeedClipAsync(stranger, now.AddMinutes(-1), title: "t2-backfill");

        using var client = ClientWithBearer(token);
        var body = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");

        FeedIds(body).Should().Equal(t0, t1, t2);
    }

    [Fact]
    public async Task ForYou_FollowedAuthorInLikedGame_AppearsOnceInTier0()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        // Followed author posts in the liked game (game 2). It must appear once, in tier 0 —
        // so it ranks above a stranger's NEWER liked-game (tier 1) clip.
        var (dual, _) = await SeedClipAsync(followed, now.AddMinutes(-5), title: "dual", gameId: 2);
        var (strangerLikedGame, _) = await SeedClipAsync(stranger, now.AddMinutes(-1), title: "t1", gameId: 2);

        using var client = ClientWithBearer(token);
        var ids = FeedIds(await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you"));

        ids.Count(id => id == dual).Should().Be(1);
        ids.Should().Equal(dual, strangerLikedGame);
    }

    [Fact]
    public async Task ForYou_LikedGameInference_SurfacesOtherClipsInThatGameInTier1()
    {
        await _fx.ResetAsync();
        var (me, token) = await SeedUserAndIssueTokenAsync("reader");
        var (stranger, _) = await SeedUserAndIssueTokenAsync("stranger");
        var now = DateTimeOffset.UtcNow;
        // me likes a clip in game 3; NO follows. A different clip in game 3 must surface in tier 1
        // above a newer backfill clip.
        var (liked, _) = await SeedClipAsync(stranger, now.AddMinutes(-9), title: "liked", gameId: 3);
        var (sameGame, _) = await SeedClipAsync(stranger, now.AddMinutes(-5), title: "same-game", gameId: 3);
        var (backfill, _) = await SeedClipAsync(stranger, now.AddMinutes(-1), title: "backfill");
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = me, ClipId = liked, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = ClientWithBearer(token);
        var ids = FeedIds(await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you"));

        // liked + sameGame are both game 3 (tier 1, newest-first); backfill is tier 2.
        ids.Should().Equal(sameGame, liked, backfill);
    }

    [Fact]
    public async Task ForYou_CrossTierPageFill_SpansBoundaryAndResumesViaCursor()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        // 2 per tier, newest-first within tier.
        var (t0a, _) = await SeedClipAsync(followed, now.AddMinutes(-1), title: "t0a");
        var (t0b, _) = await SeedClipAsync(followed, now.AddMinutes(-2), title: "t0b");
        var (t1a, _) = await SeedClipAsync(stranger, now.AddMinutes(-3), title: "t1a", gameId: 2);
        var (t1b, _) = await SeedClipAsync(stranger, now.AddMinutes(-4), title: "t1b", gameId: 2);
        var (t2a, _) = await SeedClipAsync(stranger, now.AddMinutes(-5), title: "t2a");
        var (t2b, _) = await SeedClipAsync(stranger, now.AddMinutes(-6), title: "t2b");

        using var client = ClientWithBearer(token);
        var page1 = await client.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you&limit=3");
        FeedIds(page1).Should().Equal(t0a, t0b, t1a);
        var cursor = page1.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        var page2 = await client.GetFromJsonAsync<JsonElement>(
            $"/clips/feed?source=for-you&limit=3&cursor={Uri.EscapeDataString(cursor!)}");
        FeedIds(page2).Should().Equal(t1b, t2a, t2b);
        page2.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ForYou_DeepPagination_LimitOne_WalksEveryTierAndDrains()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var now = DateTimeOffset.UtcNow;
        var (t0, _) = await SeedClipAsync(followed, now.AddMinutes(-1), title: "t0");
        var (t1, _) = await SeedClipAsync(stranger, now.AddMinutes(-2), title: "t1", gameId: 2);
        var (t2, _) = await SeedClipAsync(stranger, now.AddMinutes(-3), title: "t2");
        var expected = new[] { t0, t1, t2 };

        using var client = ClientWithBearer(token);
        var collected = new List<Guid>();
        string? cursor = null;
        for (var i = 0; i < 10; i++) // safety bound; real loop breaks on null cursor
        {
            var url = cursor is null
                ? "/clips/feed?source=for-you&limit=1"
                : $"/clips/feed?source=for-you&limit=1&cursor={Uri.EscapeDataString(cursor)}";
            var body = await client.GetFromJsonAsync<JsonElement>(url);
            collected.AddRange(FeedIds(body));
            var next = body.GetProperty("nextCursor");
            if (next.ValueKind == JsonValueKind.Null) break;
            cursor = next.GetString();
        }

        collected.Should().Equal(expected); // no repeats, no skips, drained across tier boundaries
    }

    [Fact]
    public async Task ForYou_LikedByMe_IsPerCaller()
    {
        await _fx.ResetAsync();
        var (me, token, followed, stranger) = await SeedForYouSignalsAsync();
        var (other, otherToken) = await SeedUserAndIssueTokenAsync("other");
        var (clip, _) = await SeedClipAsync(followed, DateTimeOffset.UtcNow.AddMinutes(-1), title: "clip");
        await using (var db = _fx.CreateContext())
        {
            db.Likes.Add(new Like { UserId = me, ClipId = clip, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var meClient = ClientWithBearer(token);
        using var otherClient = ClientWithBearer(otherToken);
        var meBody = await meClient.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");
        var otherBody = await otherClient.GetFromJsonAsync<JsonElement>("/clips/feed?source=for-you");

        bool LikedByMe(JsonElement body, Guid id) => body.GetProperty("items").EnumerateArray()
            .First(e => e.GetProperty("id").GetGuid() == id).GetProperty("likedByMe").GetBoolean();

        LikedByMe(meBody, clip).Should().BeTrue();
        // `other` has a like (the like-seed) so is not cold-start, and sees this clip as tier-2
        // backfill with likedByMe=false.
        LikedByMe(otherBody, clip).Should().BeFalse();
    }
```

> Note: `ForYou_LikedByMe_IsPerCaller` — `other` has no follows and no likes, so is cold-start and served the latest path; `clip` still appears (global latest) with `likedByMe=false`. The assertion holds either way.

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test server --filter "FullyQualifiedName~ClipsReadEndpointsTests&FullyQualifiedName~ForYou"`
Expected: FAIL — `source=for-you` currently falls through to global latest, so the tier-ordering, page-fill, deep-pagination, and inference tests fail (wrong order / cursor shape). The anonymous and cold-start tests may already pass.

- [ ] **Step 3: Create `ForYouFeedBuilder`**

Create `server/src/GankedTV.Api/Services/Feeds/ForYouFeedBuilder.cs`:

```csharp
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using GankedTV.Api.Endpoints;
using GankedTV.Api.Pagination;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Services.Feeds;

internal sealed record ForYouPage(IReadOnlyList<Clip> Clips, string? NextCursor);

/// <summary>
/// Builds the personalized For You feed: the public+ready feed re-ordered into relevance tiers
/// (0 = followed author, 1 = liked game, 2 = everything else), newest-first within each tier.
/// Owns tier construction, the cross-tier page fill, and the tiered cursor. Projection
/// (thumbnail signing + likedByMe) stays at the call site, so no personalized data is built
/// here — mirroring <see cref="RankedFeedBuilder"/>'s split of concerns.
/// </summary>
internal static class ForYouFeedBuilder
{
    /// <summary>
    /// Returns <c>null</c> when the caller has no follows AND no liked games — the endpoint then
    /// serves the shared latest path (cold-start), so a signal-less user gets results identical
    /// to Latest (including the cached first page).
    /// </summary>
    internal static async Task<ForYouPage?> BuildPageAsync(
        GankedTvDbContext db,
        Guid me,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var followedAuthorIds = await db.Follows.AsNoTracking()
            .Where(f => f.FollowerId == me)
            .Select(f => f.FolloweeId)
            .ToListAsync(ct);

        // A game is "liked" if it appears on >=1 clip the user has liked (there is no game-follow
        // table). The liked clip's own visibility is irrelevant — a like is a like.
        var likedGameIds = await db.Clips.AsNoTracking()
            .Where(c => c.GameId != null && db.Likes.Any(l => l.UserId == me && l.ClipId == c.Id))
            .Select(c => c.GameId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (followedAuthorIds.Count == 0 && likedGameIds.Count == 0)
        {
            return null;
        }

        var clampedLimit = Math.Clamp(
            limit ?? ClipsReadEndpoints.FeedDefaultLimit, 1, ClipsReadEndpoints.FeedMaxLimit);

        // Every public+ready clip lands in exactly one tier (highest wins), so tiers never
        // overlap and no cross-tier dedup pass is needed. `Contains` translates to SQL `IN (...)`
        // — acceptable at current scale; a heavy-follow user could move to correlated EXISTS
        // later without changing the contract (mirrors the trending feed's ~10k revisit note).
        IQueryable<Clip> TierQuery(int tier)
        {
            var q = db.Clips.AsNoTracking().WherePublicReady();
            return tier switch
            {
                0 => q.Where(c => followedAuthorIds.Contains(c.UserId)),
                1 => q.Where(c => c.GameId != null
                                  && likedGameIds.Contains(c.GameId.Value)
                                  && !followedAuthorIds.Contains(c.UserId)),
                _ => q.Where(c => !followedAuthorIds.Contains(c.UserId)
                                  && (c.GameId == null || !likedGameIds.Contains(c.GameId.Value))),
            };
        }

        var hasCursor = TieredKeysetCursor.TryParse(cursor, out var startTier, out var cursorCreatedAt, out var cursorId);
        // Clamp defends against a parseable-but-out-of-range tier; a missing/corrupt cursor already
        // yields startTier=0 from TryParse.
        startTier = Math.Clamp(startTier, 0, 2);

        // Fetch one extra across the walked tiers to detect whether a further page exists.
        var need = clampedLimit + 1;
        var collected = new List<(Clip Clip, int Tier)>(need);

        for (var tier = startTier; tier <= 2 && collected.Count < need; tier++)
        {
            var q = TierQuery(tier);
            // Keyset applies ONLY on the starting tier: lower tiers were fully drained on earlier
            // pages; higher tiers start from their newest row.
            if (hasCursor && tier == startTier)
            {
                q = q.WhereKeysetBefore(c => c.CreatedAt, c => c.Id, cursorCreatedAt, cursorId);
            }

            var rows = await q
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .IncludeFeedRelations()
                .Take(need - collected.Count)
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                collected.Add((row, tier));
            }
        }

        var hasMore = collected.Count > clampedLimit;
        var pageRows = hasMore ? collected.GetRange(0, clampedLimit) : collected;
        var clips = pageRows.Select(r => r.Clip).ToList();

        // The row's tier is recorded during the fill, so the cursor pins the correct tier even
        // when the page ends exactly on a tier boundary.
        string? nextCursor = null;
        if (hasMore)
        {
            var last = pageRows[^1];
            nextCursor = TieredKeysetCursor.Build(last.Tier, last.Clip.CreatedAt, last.Clip.Id);
        }

        return new ForYouPage(clips, nextCursor);
    }
}
```

- [ ] **Step 4: Wire the `isForYou` branch into `GetFeed`**

In `server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs`, insert the following between the end of the trending block (the `}` that closes `if (string.Equals(sort, "trending", ...))`, currently line 194) and the `// Cache only the global latest first page` comment (currently line 196):

```csharp

        // For You: personalized re-ordering of the public+ready feed into relevance tiers for a
        // signed-in caller with signals. Placed after the trending block so `source=for-you&
        // sort=trending` keeps global-trending behavior. Anonymous and no-signal (cold-start)
        // callers fall through to the latest path below — identical to a Latest request.
        var isForYou = string.Equals(source, "for-you", StringComparison.OrdinalIgnoreCase);
        if (isForYou && principal.TryGetUserId(out var me))
        {
            var forYou = await ForYouFeedBuilder.BuildPageAsync(db, me, cursor, limit, ct);
            if (forYou is not null) // null = caller has no personalization signals -> cold-start
            {
                var items = await ProjectFeedItemsAsync(forYou.Clips, principal, db, storage, s3, ct);
                return Results.Ok(new ClipFeedResponse(items, forYou.NextCursor));
            }
            // fall through to the latest path (cold-start): cached first page + keyset cursor pages
        }
```

(No `using` changes needed — `GankedTV.Api.Services.Feeds` is already imported at line 12.)

- [ ] **Step 5: Run the For You tests to verify they pass**

Run: `dotnet test server --filter "FullyQualifiedName~ClipsReadEndpointsTests&FullyQualifiedName~ForYou"`
Expected: PASS (all 8 For You tests).

- [ ] **Step 6: Run the full feed test class to confirm no regressions**

Run: `dotnet test server --filter "FullyQualifiedName~ClipsReadEndpointsTests"`
Expected: PASS (existing latest/trending/detail/share-code/featured tests unaffected).

- [ ] **Step 7: Commit**

```bash
git add server/src/GankedTV.Api/Services/Feeds/ForYouFeedBuilder.cs \
        server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs \
        server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "feat(feed): tiered For You feed builder + source=for-you endpoint branch"
```

---

### Task 3: Web API client — widen feed source union + serialization test

**Files:**
- Modify: `web/src/api/clips.ts:81` (`ClipFeedQueryBase.source`)
- Test: `web/src/api/__tests__/clips.spec.ts` (add one case in the `feed()` describe block)

**Interfaces:**
- Consumes: nothing new.
- Produces: `ClipFeedQueryBase.source?: 'public' | 'following' | 'for-you'`. Task 4 relies on `clips.feed({ source: 'for-you' })` type-checking.

- [ ] **Step 1: Write the failing test**

In `web/src/api/__tests__/clips.spec.ts`, add inside `describe('feed()', ...)` after the existing `passes the source param through when set` test (~line 65):

```typescript
    it('passes source=for-you through when set', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await clips.feed({ source: 'for-you' })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/clips/feed?source=for-you`)
    })
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd web && bun run type-check`
Expected: FAIL — `'for-you'` is not assignable to `source` (currently `'public' | 'following'`).

- [ ] **Step 3: Widen the union**

In `web/src/api/clips.ts`, change the `ClipFeedQueryBase` interface (line 78-82):

```typescript
interface ClipFeedQueryBase {
  cursor?: string | null
  limit?: number
  source?: 'public' | 'following' | 'for-you'
}
```

(No change to the `clips.feed` body — `if (query.source) params.set('source', query.source)` already serializes any string.)

- [ ] **Step 4: Run to verify it passes**

Run: `cd web && bun run type-check && bun run test:unit -- clips.spec.ts`
Expected: type-check PASS; the new test and all existing `clips.spec.ts` tests PASS.

- [ ] **Step 5: Commit**

```bash
git add web/src/api/clips.ts web/src/api/__tests__/clips.spec.ts
git commit -m "feat(feed): allow source=for-you in the web feed query type"
```

---

### Task 4: Web Home view — For You default tab

**Files:**
- Modify: `web/src/views/HomeView.vue` (lines 24-36 for the tab type/list/default; line 103 `selectTab` signature)
- Test: `web/src/views/__tests__/HomeView.spec.ts` (add one case locking the default source)

**Interfaces:**
- Consumes: `clips.feed({ source: 'for-you' })` (Task 3); `UnderlineTabs` (renders N tabs generically — no component change).
- Produces: For You is the default Home tab; For You + Latest are open to anonymous users; only Following keeps its login bounce.

- [ ] **Step 1: Re-read the design system**

Read `web/DESIGN.md` before editing the template. No new tokens or layout are introduced — this task only changes tab data + the default, which flow through the existing `UnderlineTabs`.

- [ ] **Step 2: Write the failing test**

In `web/src/views/__tests__/HomeView.spec.ts`, add a new `describe` block at the end of the file (before the final closing content):

```typescript
describe('HomeView — For You is the default tab', () => {
  it('requests the for-you feed on mount', async () => {
    feed.mockResolvedValue(makePage([makeClip('a')]))
    featured.mockResolvedValue(null)

    await mountHome()

    expect(feed).toHaveBeenCalledWith(
      expect.objectContaining({ source: 'for-you', limit: 20 }),
    )
  })
})
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd web && bun run test:unit -- HomeView.spec.ts`
Expected: FAIL — the feed is currently requested with `source: 'public'`.

- [ ] **Step 4: Update the tab type, list, and default in `HomeView.vue`**

Replace lines 24-36 (the `FeedSource` type through the `source` ref) with:

```typescript
type FeedSource = 'public' | 'following' | 'for-you'
const TABS: { key: FeedSource; label: string }[] = [
  { key: 'for-you', label: 'For You' },
  { key: 'public', label: 'Latest' },
  { key: 'following', label: 'Following' },
]

// Honour ?tab=following (used after login to bounce a viewer back to the tab they clicked while
// signed-out). Gated on auth so a signed-out user landing on `/?tab=following` directly doesn't
// hit a 401 — they fall through to For You, the default for anonymous browsing (For You serves
// global latest when signed-out).
const initialTab: FeedSource =
  route.query.tab === 'following' && auth.isAuthenticated ? 'following' : 'for-you'
const source = ref<FeedSource>(initialTab)
```

- [ ] **Step 5: Update the `selectTab` signature**

Change the `selectTab` parameter type (line 103) so the new key is accepted:

```typescript
function selectTab(next: FeedSource) {
```

(No body change: only `following` triggers the login bounce; `for-you` and `public` are open to anonymous users, exactly as `public` is today.)

- [ ] **Step 6: Run the Home view + api tests to verify green**

Run: `cd web && bun run test:unit -- HomeView.spec.ts clips.spec.ts`
Expected: PASS — the new default-tab test passes, and the four existing HomeView tests (which mock `feed` regardless of source and don't assert on tabs) stay green.

- [ ] **Step 7: Type-check, lint, and build**

Run: `cd web && bun run type-check && bun run lint && bun run build`
Expected: all PASS (`UnderlineTabs` renders three tabs generically; no template structure changed).

- [ ] **Step 8: Commit**

```bash
git add web/src/views/HomeView.vue web/src/views/__tests__/HomeView.spec.ts
git commit -m "feat(feed): make For You the default Home tab"
```

---

### Task 5: Full-suite verification (both coverage gates)

**Files:** none (verification only).

- [ ] **Step 1: Server CI mirror (build, format, test + coverage gate)**

Run: `make ci-server`
Expected: PASS — build clean, 85/85 line/branch coverage gate holds. (`TieredKeysetCursor` is fully unit-tested; `ForYouFeedBuilder` is exercised across all tier/cursor branches by the integration tests, including the cold-start `null` return and the empty-signal short-circuits.)

- [ ] **Step 2: Web CI mirror (type-check, lint, test + scoped coverage gate)**

Run: `make ci-web`
Expected: PASS — `src/api/**` coverage gate holds (the new `for-you` serialization case covers the widened union; the client body is unchanged).

- [ ] **Step 3: Manual end-to-end smoke (optional but recommended)**

With the dev stack up (`make up` + `make server` + `cd web && bun dev`): sign in as a user who follows someone and has liked a clip, open Home, confirm the **For You** tab is default and its ordering puts followed authors first, then liked-game clips, then the rest; confirm Load More paginates without repeats; confirm a signed-out visit to `/` shows For You serving global latest.

---

## Self-Review

**1. Spec coverage:**

| Spec requirement | Task |
|---|---|
| `ForYouFeedBuilder` (tier queries, page fill, cursor, `BuildPageAsync` → `ForYouPage?`, `null` on cold-start) | Task 2 |
| `TieredKeysetCursor` (`(tier, createdAt, id)`, Base64Url, forgiving `TryParse`, cross-source safety) | Task 1 |
| `GetFeed` `isForYou` branch (after trending, before cached-latest; anonymous + cold-start fall through) | Task 2 |
| Personalization on default sort only; `source=for-you&sort=trending` unchanged | Task 2 (branch placed after trending block) |
| Authenticated tiered pages bypass shared cache; anonymous/cold-start reuse cached latest | Task 2 (branch returns before cache block; cold-start/anon fall through to it) |
| `web/src/api/clips.ts` source union widened | Task 3 |
| `HomeView.vue` tabs, default `for-you`, anonymous-open, following login bounce kept | Task 4 |
| Server integration tests (anon=latest, cold-start=latest, tier order, dedup, page-fill, cursor round-trip/deep drain, liked-game inference, per-caller likedByMe) | Task 2 |
| Server unit tests (`TieredKeysetCursor` round-trip, corrupt/empty→false, plain-token→false) | Task 1 |
| Web test (`?source=for-you` serialization) | Task 3 |
| 85/85 server + scoped web coverage gates | Task 5 |

No gaps.

**2. Placeholder scan:** No TBD/TODO/"add error handling"/"similar to Task N". Every code and test step shows complete content.

**3. Type consistency:** `ForYouPage(IReadOnlyList<Clip> Clips, string? NextCursor)` and `ForYouFeedBuilder.BuildPageAsync(GankedTvDbContext, Guid, string?, int?, CancellationToken) → Task<ForYouPage?>` are used identically in the builder (Task 2 Step 3) and the endpoint call (Task 2 Step 4). `TieredKeysetCursor.Build(int, DateTimeOffset, Guid)` / `TryParse(string?, out int, out DateTimeOffset, out Guid)` match between Task 1's implementation, its tests, and the builder's use. `FeedSource = 'public' | 'following' | 'for-you'` is consistent across `clips.ts` (Task 3) and `HomeView.vue` (Task 4). `WhereKeysetBefore(createdAt, id, cursorCreatedAt, cursorId)` matches the existing `KeysetPagination` signature.
