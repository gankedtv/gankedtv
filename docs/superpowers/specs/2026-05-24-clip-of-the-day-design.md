# Clip of the Day — Featured Hero (issue #103)

## Goal

Replace `HomeView.vue`'s hero card (currently `items[0]`, the newest clip) with a daily editorial-feel "Clip of the Day" pick driven by the time-weighted trending score from #87. The pick is stable within a UTC calendar day and rolls over at UTC midnight. The hero never goes blank — if no winner exists, the web client falls back to the newest clip.

Source: [issue #103](https://github.com/gankedtv/gankedtv/issues/103); milestone Phase 4 — Scale & Grow; depends on #87.

## Approach

**B — compute on read with per-day memoization.**

A new `GET /clips/featured` endpoint runs the same time-weighted scoring as `BuildTrendingFeedAsync`, restricted to engagement within the current UTC calendar day. The selected clip `Guid` is cached in `IMemoryCache` under `featured:{yyyy-MM-dd}` with absolute expiration at the next UTC midnight. Per-request work after a cache hit is a single `Include`-hydrated fetch + signed-URL projection — `likedByMe` and presigned URLs always reflect the caller and current time.

Rejected approach A (hosted service that pre-computes daily) trades a moving part (timer drift, startup ordering, "what if it hasn't run yet" fallback) for a guarantee we don't need at current scale: the cache-on-first-request-of-the-day pattern delivers identical externally-observable behavior with one execution path.

## Server

### Endpoint

`GET /clips/featured` registered in `ClipsReadEndpoints.MapClipsReadEndpoints()`.

Response:
- `200 ClipFeedItem` — the day's featured clip, fully hydrated.
- `204 No Content` — no eligible clip (no engagement in window, or no public+ready clips at all). Web handles fallback.

Anonymous-allowed (mirrors `/clips/feed`). The caller's `ClaimsPrincipal` populates `likedByMe` only.

### Selection

In a new `BuildFeaturedClipIdAsync` helper alongside `BuildTrendingFeedAsync`:

1. Base query: `Visibility == "public" && Status == "ready"`.
2. Window: engagement since `DateTimeOffset.UtcNow.Date` (00:00 UTC of today).
3. Candidate set: clips with ANY like or view in the window (same pre-filter pattern as trending — bounds the in-memory scoring step).
4. Score (identical to trending): `(LikesInWindow * 3 + ViewsInWindow) / pow(max(0, hoursSinceCreated) + 2, 1.5)`.
5. Order: `Score DESC, LikeCount DESC, CreatedAt DESC, Id DESC`.
   - The last three are the deterministic tie-break required by the issue ("higher `likeCount`, then newer `created_at`, then `id`"). Trending only does `Score DESC, CreatedAt DESC` — featured extends it because the daily pick is a single winner, so a deterministic ordering across all ties matters.
6. Returns the top `Guid`, or `null` if the candidate set is empty.

### Caching

- Service: `IMemoryCache` (register via `AddMemoryCache()` in `Program.cs` if not already wired).
- Key: `featured:{yyyy-MM-dd}` using `DateTimeOffset.UtcNow.Date`.
- Cached value: `Guid` only — *not* the hydrated DTO. Hydration runs every request so `likedByMe` and signed URLs are always per-caller / fresh.
- Expiration: `AbsoluteExpiration = nextUtcMidnight`. The next-day request misses, recomputes, and reseats the key under the new date.
- Stale-clip handling: if the cached `Guid` no longer resolves to a `public`+`ready` clip on rehydration (deleted, unpublished, taken back to processing), evict the key and return 204 for this request. The next request misses, recomputes against current DB state, and reseats the cache. Trades one transient "fallback to newest" render for handler simplicity (no retry loop, single code path).
- No 204-caching: an empty result is *not* cached (would prevent newly-eligible clips from surfacing within the day).

### Hydration

After the cache resolves to a `Guid`, the handler reuses existing helpers:

- `db.Clips.AsNoTracking().Where(c => c.Id == id && c.Visibility == "public" && c.Status == "ready").IncludeFeedRelations().FirstOrDefaultAsync(ct)` — the re-check of visibility/status is what triggers the stale-clip eviction above.
- `ProjectFeedItemsAsync([clip], principal, db, storage, s3, ct)` produces a `List<ClipFeedItem>` of size 1.
- Return `Results.Ok(items[0])`.

### Files

- `server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs` — register route, add `GetFeatured` handler + `BuildFeaturedClipIdAsync` helper.
- `server/src/GankedTV.Api/Program.cs` — `AddMemoryCache()` if missing. (Only DI wiring — stays out of the coverage denominator.)
- `server/tests/GankedTV.Api.Tests/Endpoints/FeaturedClipEndpointTests.cs` — new test file.

## Web

### API client

`web/src/api/clips.ts`:

```ts
featured(): Promise<ClipFeedItem | null>
```

- Issues `GET /clips/featured`.
- Resolves to `null` on 204.
- Inspect `web/src/api/client.ts` for current 204 handling; if it doesn't already resolve to `null` for empty-body responses, add that branch (generic — benefits future endpoints too, doesn't bake `featured`-specific logic into the client).

### HomeView

`web/src/views/HomeView.vue`:

- Add `const featured = ref<ClipFeedItem | null>(null)`, loaded in parallel with the initial `loadMore()` call via `Promise.allSettled` so a `featured` failure can't block the feed.
- `hero` becomes `computed(() => featured.value ?? items.value[0] ?? null)`.
- `secondary`/`grid` keep slicing from `items.value` starting at index 1, unchanged. The featured pick may already appear in the feed — that's acceptable (and matches how a "pinned" hero behaves elsewhere on the web); de-duplicating the feed is out of scope.
- Hero label: render "Clip of the Day" when `featured.value` is set; keep "Featured Clip" when falling back to `items[0]`. The badge must not lie about provenance.
- A `featured` fetch failure is silent — the fallback already covers it; no error UI.
- The "Following" tab is unaffected — the featured pick is global by definition and only shows when the active source is `public`. (Behavior: featured loads once on mount; the badge/clip stays the same across tab switches because tab switching only resets `items`, not `featured`. Acceptable since the pick is genuinely global.)

### Files

- `web/src/api/clips.ts` — add `featured()`.
- `web/src/api/client.ts` — (conditional) add 204 → `null` handling if missing.
- `web/src/views/HomeView.vue` — wire `featured`, update `hero` computed, dynamic label.

## Tests

### Server (xUnit + real DB fixture)

Test file: `FeaturedClipEndpointTests.cs`. All tests use the existing `WebApplicationFactory` integration fixture (same pattern as the trending tests).

- **Empty DB returns 204** — no clips at all.
- **No engagement today returns 204** — clips exist but none have likes/views since 00:00 UTC.
- **Highest-scoring eligible clip wins** — three clips with varying like/view mixes, assert the expected winner.
- **Skips non-public clips** — an unlisted clip with the best score is not chosen.
- **Skips non-ready clips** — a processing/failed clip with the best score is not chosen.
- **Tie-break: higher likeCount wins** — two clips with identical computed scores, the one with more total likes wins.
- **Tie-break: newer createdAt wins** — identical score and likeCount, newer clip wins.
- **Tie-break: higher Id wins** — identical score, likeCount, createdAt (rare, but the test pins the contract).
- **Cache hit returns same clip within day** — call twice, second call returns the same `Guid` even after a new higher-scoring clip is inserted between calls.
- **Stale cached clip triggers eviction + recompute** — cached winner is deleted between calls; next call recomputes and returns a different (or null/204) result.
- **likedByMe reflects the calling user** — same cached pick returns `likedByMe: true` for the liker and `false` for an anonymous caller.

The "cache hit" and "stale eviction" tests need a deterministic clock or per-test `IMemoryCache` reset — easiest: register a test-scoped `IMemoryCache` (or invalidate the key directly via DI in the test). The fixture already exposes services for this kind of nudge.

### Web (Vitest)

- `clips.featured()` returns `null` when the client surfaces a 204.
- `clips.featured()` returns the parsed `ClipFeedItem` on 200.

`HomeView` integration coverage is out of scope per the existing coverage gate (`src/views/` is excluded from the threshold). A visual smoke test is in the "manual verification" section below.

## Manual verification

Per the issue's "How to test manually":

```bash
# Returns the featured clip
curl -s http://localhost:5050/clips/featured | jq '{id, title, likeCount, viewCount}'

# Second call within the same UTC day returns the same id
curl -s http://localhost:5050/clips/featured | jq '.id'

# Empty DB / no engagement returns 204
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5050/clips/featured
```

Browser: load `/`, confirm the hero is badged "Clip of the Day". With a fresh DB (no engagement), confirm the hero falls back to the newest clip and the badge reads "Featured Clip".

## Out of scope

- Hosted-service / Redis variant (approach A).
- De-duplicating the featured clip from the feed grid.
- Per-game or per-user featured picks.
- Featured-by-editorial (manual pin) override.
- Localized "day" — UTC is the only reference frame.
- Backfill: there is no historical "clip of the day for 2026-04-12" — the cache key exists only for the current day.

## Risks / accepted tradeoffs

- **Single-process cache.** Per-instance memoization means N app instances may pick different winners on the same day before all have warmed (each picks deterministically from the same scoring rules, but the *first request to each instance* materializes its own cache entry). At our current single-instance dev/prod posture this is moot; revisit when we scale horizontally.
- **First-request-of-day latency.** The first user after UTC midnight pays the scoring cost; everyone else hits cache. The candidate set is bounded by the engagement pre-filter (same bound as trending) so this is small.
- **No write-through invalidation on visibility/status changes.** A clip flipped to unlisted *after* being cached will trigger the stale-clip eviction on the next request (one extra DB roundtrip), but won't proactively re-pick. Bounded and self-healing.
