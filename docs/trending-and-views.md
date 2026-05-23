# Trending feed & view counting

How `POST /clips/{id}/view`, the `clip_views` event table, and the trending feed
(`GET /clips/feed?sort=trending&window=…`) fit together. Implemented in [#87].

[#87]: https://github.com/gankedtv/gankedtv/issues/87

## Pipeline at a glance

```text
  ┌─────────────────┐   ≥3s playback   ┌──────────────────────┐
  │  ClipView.vue   │ ───────────────► │ POST /clips/{id}/view │
  └─────────────────┘                  └──────────┬───────────┘
                                                  │ pass dedup?
                                                  ▼
                                          ┌──────────────────┐
                                          │  IMemoryCache    │  30-min sliding window
                                          │  view:{clip}:{v} │  keyed by user or IP
                                          └────────┬─────────┘
                                                   │ miss
                                                   ▼
                              ┌──────────────────────────────────────┐
                              │ TX: clips.view_count++               │
                              │     INSERT INTO clip_views(clip, ts) │
                              └──────────────────────────────────────┘
                                                   │
                                                   ▼
                              ┌──────────────────────────────────────┐
                              │ GET /clips/feed?sort=trending&window │
                              │   score = (likes×3 + views) /        │
                              │           (hours+2)^1.5              │
                              │   filtered to clips with engagement  │
                              │   in the window, top 50              │
                              └──────────────────────────────────────┘
```

## View counting

### Endpoint

`POST /clips/{id}/view`

- Anonymous-friendly — no `Authorization` header required
- Returns **`204 No Content`** on every outcome: success, dedup hit, missing clip,
  non-public clip. The client fires-and-forgets; the response carries no
  information it acts on
- Rate-limited per-IP (see below)

Files:
- Endpoint: [server/src/GankedTV.Api/Endpoints/ClipsViewEndpoints.cs](../server/src/GankedTV.Api/Endpoints/ClipsViewEndpoints.cs)
- Client call site: [web/src/views/ClipView.vue](../web/src/views/ClipView.vue) — `attachViewTracking`
- API client method: `clips.recordView(id)` in [web/src/api/clips.ts](../web/src/api/clips.ts)

### Dedup (the 30-min window)

Each view is keyed and looked up in `IMemoryCache`:

```text
key = "view:{clipId}:{viewerKey}"
viewerKey = "u:{jwt_sub}"   // authenticated
          | "ip:{remoteIp}" // anonymous
ttl       = 30 min, sliding (resets on every cache hit)
```

A cache hit → silent `204`, no counter change, no event row.
A cache miss → counter is bumped atomically, event row is inserted, **then** the
cache entry is written. Writing the cache after the transaction commits means a
transient DB failure doesn't poison the cache and silently swallow the next
30 minutes of legitimate views.

Behavioural consequences:

- A 45-minute watch session counts as **one** view (sustained viewing keeps the
  TTL sliding).
- Stepping away for 30+ minutes and coming back counts as a **new** view.
- Two different logged-out users behind the same NAT collapse to one viewer
  until they sign in.
- Two different signed-in users on the same IP each count once — auth wins over
  IP for dedup, so shared networks don't penalise distinct viewers.
- Cache is **in-process**. An API restart resets all dedup state; the next view
  from any (clip, viewer) pair counts again. Acceptable for a single-instance
  deployment; the Phase-4 follow-up (per [PLAN.md §4](../PLAN.md)) swaps for
  Redis for cluster-wide + restart-stable dedup.

> **This is not "unique viewers" forever.** It's time-windowed dedup. If you
> ever need lifetime-unique-per-account semantics (à la YouTube unique viewers),
> that's a different metric requiring a persisted `(clip_id, user_id)` table
> with a unique constraint.

### Rate limiting

Policy `clips-view` — 20 requests per IP per minute, fixed window. Wired in
[server/src/GankedTV.Api/Clips/ClipsRateLimiting.cs](../server/src/GankedTV.Api/Clips/ClipsRateLimiting.cs).

Partition key is **IP-only** even for authenticated callers — the endpoint is
anonymous-friendly so a per-user bucket wouldn't bound abuse from the dominant
(logged-out) case. The 21st request from the same IP inside a 60-second window
returns `429 Too Many Requests` with a `Retry-After` header.

The dedup window (30 min) and the rate-limit window (60 s) are independent: the
limiter counts every request (including dedup hits), the dedup decides whether
the request mutates state.

### Two pieces of state

Each non-deduped view writes to two places in a single transaction:

| Table / column | Purpose |
| --- | --- |
| `clips.view_count` (int) | Denormalised counter shown in feed/detail DTOs. Cheap reads. |
| `clip_views(id, clip_id, created_at)` | Append-only event log. Drives time-window queries (trending). |

Both updates are wrapped in `BeginTransactionAsync` → `ExecuteUpdateAsync` →
`Add()` → `SaveChangesAsync` → `CommitAsync`. If the counter update affects
zero rows (clip is missing, draft, processing, or unlisted) the transaction
short-circuits and no event row is written.

The `ExecuteUpdateAsync` predicate `Visibility = 'public' AND Status = 'ready'`
is what makes a `POST /clips/{id}/view` against an unlisted or processing clip a
silent no-op rather than a 404 — clients can't distinguish "missing" from
"not viewable" by status code, which discourages enumeration attacks.

### Indexes

Migration: [20260523154709_AddClipViewsAndLikesCreatedAtIndex](../server/src/GankedTV.Api/Data/Migrations/20260523154709_AddClipViewsAndLikesCreatedAtIndex.cs).

- `idx_clip_views_created_at` (created_at DESC) — global window scan
- `idx_clip_views_clip_id_created_at` (clip_id, created_at DESC) — per-clip aggregation
- `idx_likes_created_at_clip_id` (created_at DESC, clip_id) — likes time-window
  aggregation (the `likes` table previously had only a composite PK on
  `user_id, clip_id` — no `created_at` index — so the trending query would
  have table-scanned likes without this)

## Trending feed

### Endpoint

`GET /clips/feed?sort=trending&window=24h|7d&limit=…`

- `sort=trending` switches the handler into the time-weighted ranking branch.
- `window` is **required** when `sort=trending` — an unknown or missing value
  returns `400 Bad Request` with `code: invalid_window`. Unlike `source`
  (which falls back silently), trending without a window is meaningless and
  the client sends the value verbatim, so a 400 surfaces UI bugs instead of
  guessing.
- `limit` clamps to `[1, 50]` (lower than the default feed's 100). The Trending
  UI shows the top 10; 50 gives headroom for future top-N views.
- `cursor` is ignored — trending is a single ranked page, `nextCursor: null`.
- `sort=latest` and an omitted `sort` are identical to the pre-issue feed
  (regression-guarded by `Feed_DefaultSortLatest_UnchangedFromPreTrendingBehavior`).

Handler: `GetFeed` → `BuildTrendingFeedAsync` in
[server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs](../server/src/GankedTV.Api/Endpoints/ClipsReadEndpoints.cs).

### Score formula

```text
score = (likes_in_window × 3 + views_in_window) / (max(0, hours_since_post) + 2) ^ 1.5
```

**Numerator — engagement signal:**

- `likes_in_window` — `COUNT(*)` from `likes` where `created_at > now − window`
- `views_in_window` — `COUNT(*)` from `clip_views` where `created_at > now − window`
- Likes are weighted **3×** views — a deliberate emotional-investment vs.
  passive-play ratio. Pinned by `Trending_LikesCountedTriple_VsViews`.

**Denominator — age decay (Reddit-style):**

- `hours_since_post = (now − clip.created_at).TotalHours`, clamped at 0
- `+2` softens the t≈0 spike (a clip 0 minutes old would otherwise be
  divided by 1 and dominate; the offset is 1.5× the steepness's natural unit)
- `^1.5` is the decay exponent — half-life of relevance is approximately
  `2 × (2^(2/3) − 1) ≈ 1.17` engagement-doublings per hour-doubling

### Worked example

24-hour window. `T` = right now.

| Clip | Posted | Likes (24h) | Views (24h) | Numerator | Denom (hours+2)^1.5 | Score |
| --- | --- | --: | --: | --: | --: | --: |
| A | 1 h ago | 5 | 30 | 45 | 5.20 | **8.66** |
| B | 12 h ago | 20 | 200 | 260 | 52.38 | **4.96** |
| C | 30 min ago | 1 | 2 | 5 | 3.95 | **1.27** |
| D | 1 h ago | 0 | 0 | 0 | — | excluded (no engagement) |

Order: **A > B > C**. Clip A's recency lets fewer signals outweigh Clip B's
higher absolute count. Clip D is pre-filtered SQL-side before scoring.

### Query path

1. Apply the base feed filter (`visibility = 'public' AND status = 'ready'`, plus
   the optional `source=following` filter — trending honours it).
2. **Pre-filter SQL-side** to clips with at least one like OR one view in the
   window:
   ```csharp
   .Where(c => db.Likes.Any(l => l.ClipId == c.Id && l.CreatedAt > since)
            || db.ClipViews.Any(v => v.ClipId == c.Id && v.CreatedAt > since))
   ```
   This is the load-shedding step. Without it, scoring would touch every public
   clip even if nothing has happened to it in the window.
3. Fetch `(Clip, likes_in_window, views_in_window)` tuples to memory.
4. **Score in C#**, not SQL. Postgres has no clean `pow(double, double)` over
   interval-derived hours via EF translation; scoring in memory over a
   bounded candidate set is acceptable. The plan notes "revisit if active
   clips per window climbs past ~10k" — we're well under that.
5. Order by score desc, then `created_at` desc as tiebreaker.
6. Take top N (clamped limit), re-fetch with `IncludeFeedRelations()` (author,
   game, tags, like-state) preserving rank order.
7. Project through the shared `ProjectFeedItemsAsync` so the response DTO is
   byte-identical to the latest feed.

### Excluded clips

Trending omits:

- `visibility != 'public'` — unlisted clips never reach Trending. Direct-link
  access still works via `/clips/{id}` and `/c/{code}`.
- `status != 'ready'` — processing or failed clips don't appear.
- Clips with zero likes AND zero views in the window — even if they're brand
  new. Trending answers "what are people engaging with right now," not
  "what's new."

## Web wiring

[TrendingView.vue](../web/src/views/TrendingView.vue):

- Calls `clips.feed({ sort: 'trending', window: timeWindow.value, limit: 50 })`,
  then takes the first 10 for the leaderboard.
- `watch(timeWindow, load)` re-fetches when the user toggles `24h` ↔ `7d`.
- Only `24h` and `7d` tabs are enabled — `1h`, `30d`, `all` render dimmed/
  non-clickable until server windows expand (deliberate roadmap surface).
- Drops the pre-issue hardcoded trend-arrow indicator (▲/▼/—). The leaderboard
  rank is the v1 signal; a real `previousRank` field is a follow-up.

[ClipView.vue](../web/src/views/ClipView.vue) — view tracking:

- Attaches a `timeupdate` listener to the underlying `<video>` element after
  Plyr mounts. Plyr re-fires the same DOM event but the element listener is
  resilient to Plyr-lifecycle quirks.
- Accumulates playback time with a **per-tick delta clamped to [0, 1000 ms]**.
  This is what stops a scrub-forward from instantly crediting the gap, and a
  scrub-backward from subtracting.
- At `playedMs >= 3000`, calls `clips.recordView(clipId)` exactly once per
  mount and stores the clipId in a module-scope flag. Errors are silently
  swallowed — fire-and-forget; retrying would defeat the server's dedup +
  rate-limit.
- Detached cleanly on clip teardown (`teardownPlayer`) and on
  `onBeforeUnmount`. Switching to a different clip mid-watch resets the
  accumulator.

## Operational notes & follow-ups

What's intentionally **not** in v1, noted here so we don't relitigate it in PR
review:

- **No caching of trending results.** Each request recomputes. The candidate
  set is pre-filtered to engaged clips, and at current volume the cost is
  bounded. Add caching (per-window TTL ~60 s) when DB load justifies it.
- **No score-keyset cursor.** Trending returns a single top-N page. Deep
  pagination of a time-decaying ranked list is a UX trap (entries shift
  underneath the cursor); we deliberately ship "what's hot now" as a snapshot.
- **No `previousRank` / trend arrows.** Re-introduce when there's product
  signal for it; needs a periodic snapshot table or a delta computed from a
  prior cached run.
- **No Redis dedup.** `IMemoryCache` works for a single API instance. Two API
  pods would each maintain their own dedup state — at worst, a viewer
  alternating between pods would count twice instead of once. Phase 4.
- **No `1h` / `30d` / `all` windows.** The UI shows these tabs as disabled so
  the product intent is visible; the server returns 400 for them by design.
  Adding a window is a one-line change in `TryParseTrendingWindow`.
- **No follower/reach normalisation.** Raw engagement only. A high-follower
  author's clips will rank higher because they get more likes and views, not
  through any explicit boost.

## Tests

The numbers below are the new/changed cases — there's also the existing suite.

| Test | Locks |
| --- | --- |
| `ClipsViewEndpointsTests.RecordView_Anonymous_Returns204AndIncrementsCounter` | Happy path |
| `RecordView_RepeatedFromSameIp_IsDedupedToOne` | 30-min cache dedup |
| `RecordView_NonExistentClip_Returns204Silently` | Silent no-op spec |
| `RecordView_NonPublicClip_Returns204AndNoIncrement` | Unlisted/processing don't increment |
| `RecordView_AuthenticatedDedupsByUserNotIp` | Auth wins over IP in dedup key |
| `RecordView_ExceedingRateLimit_Returns429` | 20/min IP limit |
| `ClipsReadEndpointsTests.Trending_24h_OrdersByScore` | Score-based ordering |
| `Trending_LikesCountedTriple_VsViews` | The `×3` coefficient |
| `Trending_24hExcludesOlderEngagement_7dIncludes` | Time-window boundary |
| `Trending_ExcludesNonPublicAndNonReady` | Visibility/status filter |
| `Trending_OmitsClipsWithoutEngagementInWindow` | The engagement pre-filter |
| `Trending_InvalidWindow_Returns400` / `Trending_MissingWindow_Returns400` | 400 surface for bad windows |
| `Trending_EmptyResult_Returns200WithNoItems` | Empty-page shape |
| `Trending_LimitClampedToTrendingMax` | `[1, 50]` clamp |
| `Feed_DefaultSortLatest_UnchangedFromPreTrendingBehavior` | Regression guard for `sort=latest` and omitted sort |
| `ClipsRateLimitingTests.ResolveIpPartitionKey_*` | IP-only partition for view policy |
| `clips.spec.ts › feed › encodes sort and window for trending queries` | Web → server param wire format |
| `clips.spec.ts › recordView()` | `POST /clips/{id}/view` shape |

Server: 686 tests, coverage 94.65 line / 86.67 branch (gate 85/85).
Web: 173 tests, coverage 94.96 branch / 93.42 line on the gated paths.
