# Clip of the Day — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `GET /clips/featured` endpoint that returns a single "Clip of the Day" picked by today's time-weighted trending score (stable per UTC day, memoized in `IMemoryCache`), and wire `HomeView.vue`'s hero card to it with a "Clip of the Day" badge and a newest-clip fallback.

**Architecture:** Compute-on-read with per-UTC-day memoization. The endpoint runs the same trending score formula as `BuildTrendingFeedAsync` against engagement since 00:00 UTC, caches only the winning `Guid` keyed by date with absolute expiration at next UTC midnight, and rehydrates per request so `likedByMe` + presigned URLs stay fresh. Empty result → `204`; the web client falls back to the newest clip from `/clips/feed`. Server selection logic deterministically tie-breaks by `Score → LikeCount → CreatedAt → Id` to guarantee a single winner.

**Tech Stack:** .NET 10 (ASP.NET Core minimal APIs, EF Core, `IMemoryCache` already wired at `Program.cs:275`); Vue 3 + TypeScript + Vite + Vitest; xUnit + FluentAssertions + Postgres test fixture (`PostgresFixture` / `[Collection("Postgres")]`).

**Source spec:** `docs/superpowers/specs/2026-05-24-clip-of-the-day-design.md`

---

## File Map

**Server — create:**
- *(none — all server changes are extensions of existing files)*

**Server — modify:**
- `server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs` — register `GET /clips/featured`, add `GetFeatured` handler + `BuildFeaturedClipIdAsync` helper.
- `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs` — add featured tests under a new `// ---- GET /clips/featured ----` section, reusing `SeedClipAsync`/`SeedUserAndIssueTokenAsync`.

**Server — DO NOT touch:**
- `Program.cs` — `AddMemoryCache()` is already registered at line 275. No change needed.

**Web — modify:**
- `web/src/api/clips.ts` — add `featured()` method.
- `web/src/api/__tests__/clips.spec.ts` — add `describe('featured()')` block.
- `web/src/views/HomeView.vue` — load featured in parallel with feed, compute `hero` with fallback, switch label between "Clip of the Day" and "Featured Clip".

**Web — DO NOT touch:**
- `web/src/api/client.ts` — already returns `undefined` for `204 No Content` (line 122/138). The new `featured()` will coerce `undefined` to `null` at the call site.

---

## Task 1: Server — failing test for the empty-DB 204

**Files:**
- Modify: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs` (append at end of class, before final `}`)

- [ ] **Step 1.1: Add the section divider + first failing test**

Append to the test class (just before the closing `}` of the class):

```csharp
    // ---- GET /clips/featured ----

    [Fact]
    public async Task Featured_EmptyDb_Returns204()
    {
        await _fx.ResetAsync();
        using var client = _factory!.CreateClient();

        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
```

- [ ] **Step 1.2: Run the test, confirm it fails with 404 (endpoint not registered yet)**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_EmptyDb_Returns204"
```

Expected: FAIL — actual status `404 NotFound` (route doesn't exist), expected `204 NoContent`.

- [ ] **Step 1.3: Commit the failing test**

```bash
git add server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "test: failing test for GET /clips/featured 204 on empty DB"
```

---

## Task 2: Server — minimal endpoint that always returns 204

**Files:**
- Modify: `server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs`

- [ ] **Step 2.1: Register the route in `MapClipsReadEndpoints`**

Find this block near the top of the file (around line 33-40):

```csharp
    public static IEndpointRouteBuilder MapClipsReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips");
        group.MapGet("/feed", GetFeed);
        group.MapGet("/{id:guid}", GetDetail);
        app.MapGet("/c/{code:length(6,12)}", GetByShareCode);
        return app;
    }
```

Change to:

```csharp
    public static IEndpointRouteBuilder MapClipsReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/clips");
        group.MapGet("/feed", GetFeed);
        group.MapGet("/featured", GetFeatured);
        group.MapGet("/{id:guid}", GetDetail);
        app.MapGet("/c/{code:length(6,12)}", GetByShareCode);
        return app;
    }
```

Order matters: `/featured` must come before `/{id:guid}` so the literal route wins over the parameterized one. (`Guid.TryParse("featured")` is false so the constraint would skip it, but ordering the literal first is clearer and matches ASP.NET routing conventions.)

- [ ] **Step 2.2: Add the minimal handler stub**

Add directly above the existing `private static Task<IResult> GetDetail(...)` method (around line 278):

```csharp
    private static Task<IResult> GetFeatured(
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        IMemoryCache cache,
        CancellationToken ct) =>
        Task.FromResult<IResult>(Results.NoContent());
```

Add the `IMemoryCache` using directive at the top of the file (after the existing `using Microsoft.Extensions.Options;`):

```csharp
using Microsoft.Extensions.Caching.Memory;
```

- [ ] **Step 2.3: Run the test, confirm it passes**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_EmptyDb_Returns204"
```

Expected: PASS.

- [ ] **Step 2.4: Commit**

```bash
git add server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs
git commit -m "feat: register GET /clips/featured (returns 204 stub)"
```

---

## Task 3: Server — failing test for "highest-scoring clip wins"

**Files:**
- Modify: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs`

- [ ] **Step 3.1: Add the test**

Append below `Featured_EmptyDb_Returns204`:

```csharp
    [Fact]
    public async Task Featured_PicksHighestScoringClip()
    {
        // Three clips of identical age — engagement alone decides. The clip with the
        // most views in today's UTC window wins.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        // Use a timestamp inside today's UTC day AND within trending's typical recency
        // (5 min ago). If `now` is within 5 minutes of UTC midnight, snap to todayStart
        // so the test isn't time-of-day fragile.
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (hot, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "hot");
        var (mid, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "mid");
        var (cool, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "cool");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 20).Select(_ => new ClipView { ClipId = hot, CreatedAt = engagementAt }));
            db.ClipViews.AddRange(
                Enumerable.Range(0, 5).Select(_ => new ClipView { ClipId = mid, CreatedAt = engagementAt }));
            db.ClipViews.AddRange(
                Enumerable.Range(0, 1).Select(_ => new ClipView { ClipId = cool, CreatedAt = engagementAt }));
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(hot);
        body.GetProperty("title").GetString().Should().Be("hot");
    }
```

- [ ] **Step 3.2: Run it, confirm FAIL**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_PicksHighestScoringClip"
```

Expected: FAIL — got 204, expected 200 with body.

- [ ] **Step 3.3: Commit failing test**

```bash
git add server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "test: failing test for featured picks highest-scoring clip"
```

---

## Task 4: Server — implement selection + hydration (no caching yet)

**Files:**
- Modify: `server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs`

- [ ] **Step 4.1: Replace the stub handler with the real selection logic**

Replace the `GetFeatured` stub from Task 2 with:

```csharp
    // Daily "Clip of the Day". Selection reuses the time-weighted trending score over a
    // UTC-calendar-day window with a strict deterministic tie-break (Score → LikeCount →
    // CreatedAt → Id). Returns 204 when no public+ready clip has engagement today; the
    // web client falls back to the newest clip so the hero never goes blank.
    private static async Task<IResult> GetFeatured(
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        IMemoryCache cache,
        CancellationToken ct)
    {
        var winnerId = await BuildFeaturedClipIdAsync(db, ct);
        if (winnerId is null)
        {
            return Results.NoContent();
        }

        var clip = await db.Clips.AsNoTracking()
            .Where(c => c.Id == winnerId.Value && c.Visibility == "public" && c.Status == "ready")
            .IncludeFeedRelations()
            .FirstOrDefaultAsync(ct);

        if (clip is null)
        {
            // Cached pick was deleted / unpublished / taken back to processing between
            // selection and rehydration. No retry — surface 204 and let the next request
            // recompute under a fresh cache state. (Caching wired up in a later task.)
            return Results.NoContent();
        }

        var items = await ProjectFeedItemsAsync([clip], principal, db, storage, s3, ct);
        return Results.Ok(items[0]);
    }

    // Picks today's featured clip by the same time-weighted score used for trending,
    // but with engagement scoped to the current UTC calendar day. Deterministic
    // ordering across all ties (Score → LikeCount → CreatedAt → Id) so a single winner
    // is always pinned for the day.
    //
    // Returns null when no public+ready clip has any like/view since 00:00 UTC today.
    internal static async Task<Guid?> BuildFeaturedClipIdAsync(
        GankedTvDbContext db,
        CancellationToken ct)
    {
        var todayStart = DateTimeOffset.UtcNow.Date;

        var candidates = await db.Clips.AsNoTracking()
            .Where(c => c.Visibility == "public" && c.Status == "ready")
            .Where(c => db.Likes.Any(l => l.ClipId == c.Id && l.CreatedAt >= todayStart)
                     || db.ClipViews.Any(v => v.ClipId == c.Id && v.CreatedAt >= todayStart))
            .Select(c => new
            {
                c.Id,
                c.LikeCount,
                c.CreatedAt,
                LikesInWindow = db.Likes.Count(l => l.ClipId == c.Id && l.CreatedAt >= todayStart),
                ViewsInWindow = db.ClipViews.Count(v => v.ClipId == c.Id && v.CreatedAt >= todayStart),
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        return candidates
            .Select(r => new
            {
                r.Id,
                r.LikeCount,
                r.CreatedAt,
                Score = (r.LikesInWindow * 3 + r.ViewsInWindow)
                    / Math.Pow(Math.Max(0, (now - r.CreatedAt).TotalHours) + 2, 1.5),
            })
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.LikeCount)
            .ThenByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .First()
            .Id;
    }
```

- [ ] **Step 4.2: Run both featured tests, confirm both pass**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_"
```

Expected: 2 passing (`Featured_EmptyDb_Returns204`, `Featured_PicksHighestScoringClip`).

- [ ] **Step 4.3: Commit**

```bash
git add server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs
git commit -m "feat: GET /clips/featured selects highest-scoring clip today"
```

---

## Task 5: Server — failing tests for visibility/status filters

**Files:**
- Modify: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs`

- [ ] **Step 5.1: Add two filter tests**

Append:

```csharp
    [Fact]
    public async Task Featured_SkipsNonPublicClips()
    {
        // An unlisted clip with overwhelming engagement is never the featured pick.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (unlisted, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "unlisted", visibility: "unlisted");
        var (publicClip, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "public");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 50).Select(_ => new ClipView { ClipId = unlisted, CreatedAt = engagementAt }));
            db.ClipViews.Add(new ClipView { ClipId = publicClip, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(publicClip);
    }

    [Fact]
    public async Task Featured_SkipsNonReadyClips()
    {
        // A processing/failed clip with overwhelming engagement is never the featured pick.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (processing, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "processing", status: "processing");
        var (ready, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "ready");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 50).Select(_ => new ClipView { ClipId = processing, CreatedAt = engagementAt }));
            db.ClipViews.Add(new ClipView { ClipId = ready, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(ready);
    }
```

- [ ] **Step 5.2: Run the new tests — they should already pass (the visibility/status filters are already in `BuildFeaturedClipIdAsync`)**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_Skips"
```

Expected: 2 passing. (TDD note: these are coverage tests for behavior already implemented in Task 4. Including them as separate cases pins the contract explicitly.)

- [ ] **Step 5.3: Commit**

```bash
git add server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "test: featured skips non-public and non-ready clips"
```

---

## Task 6: Server — failing test for "no engagement today" returns 204

**Files:**
- Modify: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs`

- [ ] **Step 6.1: Add the test**

```csharp
    [Fact]
    public async Task Featured_NoEngagementToday_Returns204()
    {
        // Clips exist but none have likes/views since 00:00 UTC today. Server returns
        // 204; the web client is responsible for falling back to "newest clip" so the
        // hero never goes blank.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var (clipId, _) = await SeedClipAsync(userId, now.AddDays(-10), title: "old");

        await using (var db = _fx.CreateContext())
        {
            // Engagement strictly before today's UTC start.
            db.ClipViews.Add(new ClipView { ClipId = clipId, CreatedAt = now.Date.AddSeconds(-1) });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
```

- [ ] **Step 6.2: Run it**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_NoEngagementToday"
```

Expected: PASS — `BuildFeaturedClipIdAsync` already filters engagement to `>= todayStart`, so yesterday's view doesn't put the clip into the candidate set.

- [ ] **Step 6.3: Commit**

```bash
git add server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "test: featured returns 204 when no engagement falls in today's UTC window"
```

---

## Task 7: Server — failing tests for deterministic tie-break

**Files:**
- Modify: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs`

- [ ] **Step 7.1: Add three tie-break tests**

```csharp
    [Fact]
    public async Task Featured_TieBreak_HigherLikeCountWins()
    {
        // Two clips with identical engagement-in-window (identical score) and identical
        // ages. Total LikeCount (denormalized, includes pre-today likes) is the next
        // tie-breaker per the issue contract.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var (likerA, _) = await SeedUserAndIssueTokenAsync("liker-a");
        var (likerB, _) = await SeedUserAndIssueTokenAsync("liker-b");
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;
        var sharedCreatedAt = now.AddHours(-1);

        var (lowLikes, _) = await SeedClipAsync(userId, sharedCreatedAt, title: "low-likes");
        var (highLikes, _) = await SeedClipAsync(userId, sharedCreatedAt, title: "high-likes");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = lowLikes, CreatedAt = engagementAt });
            db.ClipViews.Add(new ClipView { ClipId = highLikes, CreatedAt = engagementAt });

            // Bump the denormalized LikeCount on highLikes only. Pre-today like rows
            // exist on this clip but don't affect today's score; they DO affect
            // LikeCount, which is the tie-breaker.
            var highClip = await db.Clips.FirstAsync(c => c.Id == highLikes);
            highClip.LikeCount = 5;
            var lowClip = await db.Clips.FirstAsync(c => c.Id == lowLikes);
            lowClip.LikeCount = 1;
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(highLikes);
    }

    [Fact]
    public async Task Featured_TieBreak_NewerCreatedAtWinsWhenLikesEqual()
    {
        // Identical score, identical LikeCount → newer CreatedAt wins.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        // Both clips have CreatedAt within the same hour so the (hours+2)^1.5 denominator
        // is identical to 4+ decimal places — scores match. (Different CreatedAt values
        // produce slightly different scores in principle; pick values close enough that
        // the score difference is < double precision noise. 1 second apart at ~1h age:
        // score delta is ~1e-10, well below tie-break sensitivity.)
        var (older, _) = await SeedClipAsync(userId, now.AddHours(-1).AddSeconds(-1), title: "older");
        var (newer, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "newer");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = older, CreatedAt = engagementAt });
            db.ClipViews.Add(new ClipView { ClipId = newer, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(newer);
    }

    [Fact]
    public async Task Featured_TieBreak_HigherIdWinsWhenAllElseEqual()
    {
        // Identical score, LikeCount, AND CreatedAt → higher Guid wins. Final
        // deterministic tie-breaker so the daily pick is always reproducible.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;
        var sharedCreatedAt = now.AddHours(-1);

        var (a, _) = await SeedClipAsync(userId, sharedCreatedAt, title: "a");
        var (b, _) = await SeedClipAsync(userId, sharedCreatedAt, title: "b");
        var expectedWinner = a.CompareTo(b) > 0 ? a : b;

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = a, CreatedAt = engagementAt });
            db.ClipViews.Add(new ClipView { ClipId = b, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/clips/featured");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(expectedWinner);
    }
```

- [ ] **Step 7.2: Run them — all should pass against the Task 4 implementation**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_TieBreak"
```

Expected: 3 passing.

- [ ] **Step 7.3: Commit**

```bash
git add server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "test: featured deterministic tie-break (likes -> createdAt -> id)"
```

---

## Task 8: Server — failing test for "cache pins the pick within a day"

**Files:**
- Modify: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs`

- [ ] **Step 8.1: Add the cache test**

```csharp
    [Fact]
    public async Task Featured_CachedWithinSameDay_ReturnsSameClipEvenAfterBetterContender()
    {
        // First call computes the winner and caches under featured:{yyyy-MM-dd}.
        // A new clip inserted afterwards with much higher engagement should NOT
        // become the featured pick on a second call within the same day.
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (original, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "original");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = original, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var firstResp = await client.GetAsync("/clips/featured");
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await firstResp.Content.ReadFromJsonAsync<JsonElement>();
        firstBody.GetProperty("id").GetGuid().Should().Be(original);

        // Insert a clip with overwhelming engagement.
        var (challenger, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "challenger");
        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.AddRange(
                Enumerable.Range(0, 1000).Select(_ => new ClipView { ClipId = challenger, CreatedAt = engagementAt }));
            await db.SaveChangesAsync();
        }

        var secondResp = await client.GetAsync("/clips/featured");
        secondResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await secondResp.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("id").GetGuid().Should().Be(original, "the cache pins the pick within the UTC day");
    }
```

- [ ] **Step 8.2: Run it, confirm FAIL**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_Cached"
```

Expected: FAIL — second call returns `challenger` because there's no cache yet.

- [ ] **Step 8.3: Commit failing test**

```bash
git add server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "test: failing test for featured cache pinning within a UTC day"
```

---

## Task 9: Server — implement the day-keyed memory cache

**Files:**
- Modify: `server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs`

- [ ] **Step 9.1: Replace `GetFeatured` with the caching version**

Replace the existing `GetFeatured` handler with:

```csharp
    // Daily "Clip of the Day". Selection reuses the time-weighted trending score over a
    // UTC-calendar-day window with a strict deterministic tie-break (Score → LikeCount →
    // CreatedAt → Id). The winning Guid (not the hydrated DTO) is memoized under
    // featured:{yyyy-MM-dd} with absolute expiration at next UTC midnight so the pick
    // rolls over deterministically. Hydration runs every request so likedByMe + signed
    // URLs stay fresh. Returns 204 when no public+ready clip has engagement today; the
    // web client falls back to the newest clip so the hero never goes blank.
    private static async Task<IResult> GetFeatured(
        ClaimsPrincipal principal,
        GankedTvDbContext db,
        IObjectStorageService storage,
        IOptions<S3Options> s3,
        IMemoryCache cache,
        CancellationToken ct)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var cacheKey = $"featured:{today:yyyy-MM-dd}";

        Guid? winnerId;
        if (cache.TryGetValue(cacheKey, out Guid cachedId))
        {
            winnerId = cachedId;
        }
        else
        {
            winnerId = await BuildFeaturedClipIdAsync(db, ct);
            if (winnerId is not null)
            {
                // Cache only on a hit. Caching null/204 would prevent newly-eligible
                // clips from surfacing within the day.
                cache.Set(cacheKey, winnerId.Value, new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = today.AddDays(1), // next UTC midnight
                });
            }
        }

        if (winnerId is null)
        {
            return Results.NoContent();
        }

        var clip = await db.Clips.AsNoTracking()
            .Where(c => c.Id == winnerId.Value && c.Visibility == "public" && c.Status == "ready")
            .IncludeFeedRelations()
            .FirstOrDefaultAsync(ct);

        if (clip is null)
        {
            // Cached pick was deleted / unpublished / taken back to processing. Evict
            // the stale key so the next request recomputes against current DB state;
            // surface 204 for this request (no retry — keeps the handler simple).
            cache.Remove(cacheKey);
            return Results.NoContent();
        }

        var items = await ProjectFeedItemsAsync([clip], principal, db, storage, s3, ct);
        return Results.Ok(items[0]);
    }
```

- [ ] **Step 9.2: Run the cache test, confirm it now passes**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_Cached"
```

Expected: PASS. Note: `IMemoryCache` is a singleton in DI, and the test factory reuses the same DI container across `client.GetAsync` calls within a single test method, so cache state persists between the two HTTP calls.

- [ ] **Step 9.3: Run ALL featured tests, confirm green**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_"
```

Expected: 8 passing.

- [ ] **Step 9.4: Commit**

```bash
git add server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs
git commit -m "feat: cache featured clip pick per UTC day in IMemoryCache"
```

---

## Task 10: Server — failing test for stale-cache eviction

**Files:**
- Modify: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs`

- [ ] **Step 10.1: Add the test**

```csharp
    [Fact]
    public async Task Featured_StaleCachedClip_EvictsAndReturns204()
    {
        // First call caches the winner. Then the clip is hard-deleted. The next call
        // should detect the stale cache (rehydration finds nothing), evict the key,
        // and return 204. (A follow-up call would then re-pick from current state.)
        await _fx.ResetAsync();
        var (userId, _) = await SeedUserAndIssueTokenAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (clipId, _) = await SeedClipAsync(userId, now.AddHours(-1), title: "doomed");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = clipId, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        var firstResp = await client.GetAsync("/clips/featured");
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Hard-delete the cached winner. Delete ClipViews + Clip in order to satisfy FK.
        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.RemoveRange(db.ClipViews.Where(v => v.ClipId == clipId));
            await db.SaveChangesAsync();
            db.Clips.Remove(await db.Clips.FirstAsync(c => c.Id == clipId));
            await db.SaveChangesAsync();
        }

        var secondResp = await client.GetAsync("/clips/featured");
        secondResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
```

- [ ] **Step 10.2: Run it, confirm PASS** (the cache eviction was already implemented in Task 9)

```bash
dotnet test server --filter "FullyQualifiedName~Featured_StaleCachedClip"
```

Expected: PASS.

- [ ] **Step 10.3: Commit**

```bash
git add server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "test: featured evicts stale cache entry when winner is deleted"
```

---

## Task 11: Server — failing test for per-caller likedByMe

**Files:**
- Modify: `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs`

- [ ] **Step 11.1: Add the test**

```csharp
    [Fact]
    public async Task Featured_LikedByMe_ReflectsCallingUserDespiteCachedPick()
    {
        // The cache stores only the Guid, so likedByMe is recomputed every request.
        // Same pick, two callers, different likedByMe.
        await _fx.ResetAsync();
        var (authorId, _) = await SeedUserAndIssueTokenAsync("author");
        var (likerId, likerToken) = await SeedUserAndIssueTokenAsync("liker");
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.Date;
        var engagementAt = now.AddMinutes(-5) >= todayStart ? now.AddMinutes(-5) : todayStart;

        var (clipId, _) = await SeedClipAsync(authorId, now.AddHours(-1), title: "liked");

        await using (var db = _fx.CreateContext())
        {
            db.ClipViews.Add(new ClipView { ClipId = clipId, CreatedAt = engagementAt });
            db.Likes.Add(new Like { UserId = likerId, ClipId = clipId, CreatedAt = engagementAt });
            await db.SaveChangesAsync();
        }

        // First call: anonymous → likedByMe should be false
        using var anonClient = _factory!.CreateClient();
        var anonResp = await anonClient.GetAsync("/clips/featured");
        anonResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var anonBody = await anonResp.Content.ReadFromJsonAsync<JsonElement>();
        anonBody.GetProperty("likedByMe").GetBoolean().Should().BeFalse();

        // Second call: liker (same cached pick) → likedByMe should be true
        using var likerClient = ClientWithBearer(likerToken);
        var likerResp = await likerClient.GetAsync("/clips/featured");
        likerResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var likerBody = await likerResp.Content.ReadFromJsonAsync<JsonElement>();
        likerBody.GetProperty("id").GetGuid().Should().Be(clipId, "cached pick is shared across callers");
        likerBody.GetProperty("likedByMe").GetBoolean().Should().BeTrue();
    }
```

- [ ] **Step 11.2: Run it, confirm PASS**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_LikedByMe"
```

Expected: PASS.

- [ ] **Step 11.3: Final server test sweep — full file**

```bash
dotnet test server --filter "FullyQualifiedName~Featured_"
```

Expected: 10 passing.

- [ ] **Step 11.4: Commit**

```bash
git add server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs
git commit -m "test: featured rehydrates likedByMe per caller despite cached pick"
```

---

## Task 12: Server — coverage gate check

- [ ] **Step 12.1: Run the full server test suite with coverage gate**

From the repo root:

```bash
dotnet test server /p:CollectCoverage=true /p:Threshold=85%2C85
```

Expected: ALL tests pass; coverage report meets the 85% line / 85% branch threshold. The new `BuildFeaturedClipIdAsync` + cache-eviction branches in `GetFeatured` are covered by tasks 3-11; if the gate fails, inspect the coverage report (under `server/tests/GankedTV.Api.Tests/coverage.cobertura.xml`) and add a missing edge case test (most likely the "candidates list empty after the where-filter but before the score-and-sort" path — already covered by `Featured_NoEngagementToday_Returns204`, but double-check).

- [ ] **Step 12.2: No commit if it passes — proceed to Task 13**

---

## Task 13: Web — failing test for `clips.featured()`

**Files:**
- Modify: `web/src/api/__tests__/clips.spec.ts`

- [ ] **Step 13.1: Add a `describe('featured()')` block**

Insert immediately after the closing `})` of `describe('feed()', () => {...})`:

```typescript
  describe('featured()', () => {
    it('GETs /clips/featured and returns the parsed item on 200', async () => {
      const featured = {
        id: 'clip-1',
        title: 'Hot Pick',
        description: null,
        thumbnailUrl: 'https://example.test/thumb.jpg',
        durationSecs: 30,
        viewCount: 100,
        likeCount: 10,
        createdAt: '2026-05-24T00:00:00Z',
        author: { id: 'u-1', username: 'alice', avatarUrl: null },
        game: null,
        tags: [],
        likedByMe: false,
        shareCode: 'abc123',
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(featured)),
      )

      const result = await clips.featured()

      expect(result).toEqual(featured)
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/clips/featured`)
    })

    it('returns null when the server responds with 204 No Content', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(null, { status: 204, headers: { 'content-length': '0' } }),
        ),
      )

      const result = await clips.featured()

      expect(result).toBeNull()
    })
  })
```

- [ ] **Step 13.2: Run the new tests, confirm FAIL**

```bash
cd web && bun run test:unit -- --filter="featured"
```

Expected: FAIL — `clips.featured is not a function`.

- [ ] **Step 13.3: Commit failing test**

```bash
git add web/src/api/__tests__/clips.spec.ts
git commit -m "test: failing test for web clips.featured()"
```

---

## Task 14: Web — implement `clips.featured()`

**Files:**
- Modify: `web/src/api/clips.ts`

- [ ] **Step 14.1: Add the method to the `clips` object**

Insert immediately after the existing `feed(...)` method (just before `recordView`):

```typescript
  // GET /clips/featured — daily "Clip of the Day" pick. Returns null on 204
  // (no eligible clip today). HomeView falls back to the newest clip from
  // /clips/feed when this is null so the hero never goes blank.
  featured(): Promise<ClipFeedItem | null> {
    // The api() client returns undefined for 204 (see client.ts); normalize to
    // null so callers can use the explicit `null` sentinel.
    return api<ClipFeedItem | undefined>('/clips/featured').then((r) => r ?? null)
  },
```

- [ ] **Step 14.2: Run the featured tests, confirm PASS**

```bash
cd web && bun run test:unit -- --filter="featured"
```

Expected: 2 passing.

- [ ] **Step 14.3: Run the full clips spec to make sure nothing else broke**

```bash
cd web && bun run test:unit -- clips
```

Expected: all clips tests pass.

- [ ] **Step 14.4: Type check**

```bash
cd web && bun run type-check
```

Expected: no errors.

- [ ] **Step 14.5: Commit**

```bash
git add web/src/api/clips.ts
git commit -m "feat: web clips.featured() returns ClipFeedItem or null"
```

---

## Task 15: Web — wire `HomeView` to featured with fallback + dynamic label

**Files:**
- Modify: `web/src/views/HomeView.vue`

This task has no Vitest coverage (`src/views/` is excluded from the coverage gate per `web/vitest.config.ts`). Visual verification is in Task 16.

- [ ] **Step 15.1: Add the featured ref, load in parallel, and update the hero computed**

In the `<script setup lang="ts">` block:

Find:
```typescript
const items = ref<ClipFeedItem[]>([])
const cursor = ref<string | null>(null)
const loading = ref(false)
```

Insert immediately after:
```typescript
// Daily "Clip of the Day" pick. Loaded once on mount in parallel with the feed.
// Survives tab switches because it's a global pick, not per-source.
const featured = ref<ClipFeedItem | null>(null)
```

Find:
```typescript
// Hero is the newest ready clip; secondary uses the next chunk. The server returns
// items ordered by createdAt desc so position 0 is always the freshest.
const hero = computed(() => items.value[0] ?? null)
const secondary = computed(() => items.value.slice(1, 5))
const grid = computed(() => items.value.slice(5))
```

Replace with:
```typescript
// Hero prefers today's featured pick (computed server-side via /clips/featured)
// and falls back to items[0] (newest ready clip) so the hero never goes blank
// — handles fresh-platform / pre-engagement / featured-fetch-failed cases.
const hero = computed<ClipFeedItem | null>(() => featured.value ?? items.value[0] ?? null)
// True only when the hero is actually today's featured pick — the badge must
// not lie about provenance.
const heroIsFeatured = computed(() => featured.value !== null)
const secondary = computed(() => items.value.slice(1, 5))
const grid = computed(() => items.value.slice(5))
```

- [ ] **Step 15.2: Load featured on mount, in parallel with the feed**

Find:
```typescript
onMounted(loadMore)
```

Replace with:
```typescript
async function loadFeatured() {
  try {
    featured.value = await clips.featured()
  } catch (err) {
    // Silent failure — hero falls back to items[0]. No user-facing error.
    console.error('featured: load failed', err)
    featured.value = null
  }
}

onMounted(() => {
  // Independent loads — featured failure must not block the feed and vice versa.
  void Promise.allSettled([loadMore(), loadFeatured()])
})
```

- [ ] **Step 15.3: Swap the hero badge copy based on `heroIsFeatured`**

In the `<template>`, find:

```vue
              <div class="font-mono text-[11px] uppercase tracking-[0.15em] text-neon">
                Featured Clip
              </div>
```

Replace with:

```vue
              <div class="font-mono text-[11px] uppercase tracking-[0.15em] text-neon">
                {{ heroIsFeatured ? 'Clip of the Day' : 'Featured Clip' }}
              </div>
```

- [ ] **Step 15.4: Type check + lint**

```bash
cd web && bun run type-check && bun run lint
```

Expected: no errors.

- [ ] **Step 15.5: Run the unit test suite (HomeView has none, but check nothing else regressed)**

```bash
cd web && bun run test:unit
```

Expected: all pass.

- [ ] **Step 15.6: Commit**

```bash
git add web/src/views/HomeView.vue
git commit -m "feat: HomeView hero shows Clip of the Day with newest-clip fallback"
```

---

## Task 16: Manual verification

These steps confirm the feature works end-to-end. Per project CLAUDE.md, "For UI or frontend changes, start the dev server and use the feature in a browser before reporting the task as complete."

- [ ] **Step 16.1: Start the dev stack**

```bash
make up
make server &
cd web && bun dev &
```

Wait for both to be healthy (API on `:5050`, web on `:5173`).

- [ ] **Step 16.2: Seed sample data**

```bash
make seed
```

- [ ] **Step 16.3: Verify the endpoint with curl**

```bash
# Returns a clip (likely the seeded one with engagement) OR 204 (if seed has no
# engagement today — that's an acceptable outcome).
curl -s -w '\nHTTP %{http_code}\n' http://localhost:5050/clips/featured | head -20

# Second call within the same UTC day returns the same id (or same 204).
curl -s http://localhost:5050/clips/featured | jq '.id' 2>/dev/null
```

Expected: 200 with a hydrated `ClipFeedItem` shape OR 204 (depending on seed data). Either is correct.

- [ ] **Step 16.4: Force a winner by recording views via the existing endpoint**

If step 16.3 returned 204, generate some engagement on a seeded clip:

```bash
# Replace CLIP_ID with an id from `curl -s http://localhost:5050/clips/feed | jq '.items[0].id'`
CLIP_ID=$(curl -s http://localhost:5050/clips/feed | jq -r '.items[0].id')
curl -s -X POST "http://localhost:5050/clips/${CLIP_ID}/view" -w '%{http_code}\n'
```

Then re-fetch featured — should now be 200 with that clip id (after the per-IP view dedup window — may need a second clip to test stably).

- [ ] **Step 16.5: Verify in browser**

Open `http://localhost:5173/`. Confirm:

- Hero card renders.
- Badge above the title reads **"Clip of the Day"** when `/clips/featured` returned 200, or **"Featured Clip"** when it returned 204 (browser DevTools Network tab confirms which).
- Page does not crash, no console errors related to featured.
- Switching tabs (Latest ↔ Following, if signed in) does not blank out the hero.

- [ ] **Step 16.6: Confirm the empty-DB fallback path**

```bash
make clean && make up && make server &
# Don't seed. Open / in browser.
```

Expected: hero shows "No clips yet — be the first." empty state (the existing path — featured loads, returns 204, falls back to `items[0]` which is also undefined, falls through to the empty state). No crashes.

- [ ] **Step 16.7: Restore data**

```bash
make seed
```

---

## Task 17: Full CI sweep

- [ ] **Step 17.1: Run the full local CI mirror**

```bash
make ci
```

Expected: server format/build/test+coverage AND web lint/type-check/test+coverage all pass.

- [ ] **Step 17.2: Quick scope review**

Verify the diff is contained to the files in the File Map at the top of this plan:

```bash
git diff main --stat
```

Expected file list:
- `docs/superpowers/specs/2026-05-24-clip-of-the-day-design.md` (new)
- `docs/superpowers/plans/2026-05-24-clip-of-the-day.md` (new)
- `server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs` (modified)
- `server/tests/GankedTV.Api.Tests/Integration/Endpoints/ClipsReadEndpointsTests.cs` (modified)
- `web/src/api/clips.ts` (modified)
- `web/src/api/__tests__/clips.spec.ts` (modified)
- `web/src/views/HomeView.vue` (modified)

No other files should appear.

- [ ] **Step 17.3: Done — feature is ready to ship**

The branch `103-server-web-clip-of-the-day-featured-hero-clip` now contains a complete, tested implementation of issue #103.
