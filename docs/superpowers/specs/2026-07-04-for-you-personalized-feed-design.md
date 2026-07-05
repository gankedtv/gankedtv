# For You personalized feed (`source=for-you`) — issue #158

## Goal

Give the Home feed a real **For You** source: personalized clip ordering for signed-in users,
driven by who they follow and which games they engage with, while anonymous callers transparently
get the latest public feed under the same contract. Cursor pagination is preserved end-to-end and
the server coverage gate stays at 85/85.

Source: [issue #158](https://github.com/gankedtv/gankedtv/issues/158); labels `enhancement`,
`area:server`, `area:web`.

## Signals actually available

The issue's phrasing ("followed authors + followed/liked games") predates the current schema.
What the data model actually supports:

- **`Follows`** — user→user only. There is **no game-follow** table. "Followed games" is not a thing.
- **`Likes`** — user→clip, timestamped. "Liked games" is therefore *inferred*: a game that appears
  on ≥1 clip the user has liked.
- **`ClipView`** has **no `UserId`** (append-only anonymous event rows) — so the user's own watch
  history is not a usable per-user signal. Only global / time-windowed engagement exists.

So the two personalization signals are: **authors I follow** and **games I've liked**.

## Approach — relevance-tiered feed, recency within tier

For You is **not** a new engagement score. It is the existing public feed **re-ordered into
relevance tiers**, newest-first inside each tier. This is the only model that satisfies all of the
issue's constraints simultaneously: personalized *ordering*, true keyset (cursor) pagination, a
cold-start blend, and a transparent anonymous fallback.

Every public+ready clip belongs to exactly **one** tier for the requesting user (highest wins):

| Tier | Membership | Signal |
|------|------------|--------|
| 0 | clip author is someone I **follow** | explicit intent |
| 1 | clip's **game** is one I've liked (and author is *not* in tier 0) | topical affinity |
| 2 | everything else public+ready (global-latest **backfill**) | keeps the feed full |

**Order:** `tier ASC, createdAt DESC, id DESC`.

The blend and cold-start fall out for free: a user with zero follows and zero liked games has empty
tiers 0 and 1, so their entire feed is tier 2 = global latest — identical to what anonymous users
and the Latest tab see.

### Rejected alternatives

- **Engagement-weighted score (trending-style ranking).** Richer ordering, but a time-decayed score
  can't be keyset-paginated (scores drift with "now"), so it would break the explicit "keep cursor
  pagination" requirement past page 1 — trending itself sidesteps this by being a single page.
- **Lean recency union (candidate set = follows ∪ liked-games, ordered purely by recency).** Simpler,
  but for partial-signal users the feed can run dry with no backfill, and it doesn't rank follows
  above liked-game clips. The tiered model is a strict superset for modest extra code.

## Server

### Endpoint behavior — `GET /clips/feed?source=for-you`

Handled in `ClipsReadEndpoints.GetFeed`. `source` matching stays lenient/case-insensitive (as
`following` is today); `for-you` currently falls through to global latest, so this change is purely
additive and backward-compatible.

- **Anonymous** (no user id) → global latest. Reuses the existing latest path (including the cached
  first page). No 401 — same contract as an unauthenticated Latest request.
- **Authenticated, no signals** (no follows *and* no liked games) → same latest path (cold-start).
  Detected up front by the builder returning `null`; the handler then falls through to the existing
  latest branch, so cold-start users get the fast cached first page and results identical to Latest.
- **Authenticated, has signals** → tiered builder. Per-user, so it **bypasses the shared feed cache**
  (same rule as the `following` source).

Personalization applies to the default (latest) sort only. `sort=trending` is unaffected — a
`source=for-you&sort=trending` request keeps today's behavior (global trending), which the web
client never actually sends.

Handler wiring (inside the existing non-trending branch of `GetFeed`, before the cached-latest
block):

```csharp
if (isForYou && principal.TryGetUserId(out var me))
{
    var forYou = await ForYouFeedBuilder.BuildPageAsync(db, me, cursor, limit, ct);
    if (forYou is not null) // null = caller has no personalization signals → cold-start
    {
        var items = await ProjectFeedItemsAsync(forYou.Clips, principal, db, storage, s3, ct);
        return Results.Ok(new ClipFeedResponse(items, forYou.NextCursor));
    }
    // fall through to the latest path (cold-start): cached first page + keyset cursor pages
}
```

Anonymous `for-you` and cold-start `for-you` both land on the existing latest path unchanged
(cached first page when `cursor is null`, `BuildFeedPageAsync` for cursor pages).

### `ForYouFeedBuilder` (new — `server/src/GankedTV.Api/Services/Feeds/ForYouFeedBuilder.cs`)

A static helper mirroring `RankedFeedBuilder`'s style: it owns tier construction, the cross-tier
page fill, and the tiered cursor; it returns tier-ordered, feed-hydrated `Clip`s + the next cursor.
Projection (`ProjectFeedItemsAsync` → thumbnail signing + `likedByMe`) stays at the call site, so no
personalized data is ever built here.

```csharp
public sealed record ForYouPage(IReadOnlyList<Clip> Clips, string? NextCursor);

// Returns null when the user has no follows AND no liked games (cold-start → caller uses latest).
public static async Task<ForYouPage?> BuildPageAsync(
    GankedTvDbContext db, Guid me, string? cursor, int? limit, CancellationToken ct);
```

Algorithm:

1. **Signals.** Materialize two lists:
   - `followedAuthorIds` = `Follows.Where(f => f.FollowerId == me).Select(f => f.FolloweeId)`.
   - `likedGameIds` = distinct non-null `GameId` of clips the user has liked
     (`Likes.Where(l => l.UserId == me)` joined to `Clips`, `GameId != null`, `Distinct()`).
   - If **both** are empty → return `null` (cold-start).

2. **Tier candidate queries** (each starts from `db.Clips.AsNoTracking().WherePublicReady()`; every
   clip lands in exactly one tier, so tiers never overlap and no cross-tier dedup pass is needed):
   - **T0:** `followedAuthorIds.Contains(c.UserId)`
   - **T1:** `c.GameId != null && likedGameIds.Contains(c.GameId.Value) && !followedAuthorIds.Contains(c.UserId)`
   - **T2:** `!followedAuthorIds.Contains(c.UserId) && (c.GameId == null || !likedGameIds.Contains(c.GameId.Value))`

3. **Cross-tier page fill.** Parse the cursor to `(startTier, hasWithin, cursorCreatedAt, cursorId)`
   (default `startTier=0`, `hasWithin=false`). Walking tiers `startTier..2`, order each by
   `CreatedAt DESC, Id DESC`, apply `WhereKeysetBefore` **only** on the starting tier (lower tiers were
   fully drained on earlier pages; higher tiers start from newest), `IncludeFeedRelations()`, and
   `Take(need - collected)` where `need = limit + 1`. Stop once `need` rows are collected or all tiers
   are exhausted. Track each row's tier.

4. **Slice + cursor.** `hasMore = collected > limit`; `page = collected.Take(limit)`. `NextCursor` =
   `TieredKeysetCursor.Build(lastTier, lastCreatedAt, lastId)` when `hasMore`, else `null`. The row's
   tier is recorded during the fill so the cursor pins the correct tier even when a page spans a
   boundary.

`Contains` on the two id lists translates to SQL `IN (...)`. Acceptable at current scale; a scaling
caveat (mirroring the trending feed's "revisit past ~10k" note) is documented in code — a heavy-follow
user could be migrated to correlated `EXISTS` later without changing the contract.

### `TieredKeysetCursor` (new — `server/src/GankedTV.Api/Pagination/TieredKeysetCursor.cs`)

Extends the opaque token to a `(tier, createdAt, id)` triple: payload
`{tier}_{createdAt:O}_{id:D}`, Base64Url-encoded (same scheme as `KeysetCursor`). Neither the `O`
date format nor a `D`-format Guid contains `_`, so `Split('_', 3)` decodes unambiguously.

- `Build(int tier, DateTimeOffset createdAt, Guid id)` → token.
- `TryParse(string? raw, out int tier, out DateTimeOffset createdAt, out Guid id)` → `false` on
  null/empty/corrupt, so a missing or malformed cursor **silently starts from tier 0** (same
  forgiving contract as `KeysetCursor` today).

Cross-source safety: a plain `KeysetCursor` token fed to For You fails `TryParse` → tier 0 restart;
a tiered token fed to Latest/Following fails `KeysetCursor.TryParse` (it would parse the leading
tier int as a date and fail) → no-cursor restart. Neither crashes.

### Caching

Authenticated tiered pages are per-user and bypass the shared cache (like `following`). The
anonymous and cold-start cases reuse the existing cached latest first page — no new cache surface,
no new invalidation rules.

### Files (server)

- `Services/Feeds/ForYouFeedBuilder.cs` — **new**: tier queries, page fill, cursor handling.
- `Pagination/TieredKeysetCursor.cs` — **new**: `(tier, createdAt, id)` token.
- `Endpoints/ClipsReadEndpoints.cs` — add the `isForYou` branch in `GetFeed`; no changes to
  trending/following/latest paths.

## Web

- **`web/src/api/clips.ts`** — widen `ClipFeedQueryBase.source` to
  `'public' | 'following' | 'for-you'`. Serialization is already generic
  (`if (query.source) params.set('source', query.source)`), so no runtime change.
- **`web/src/views/HomeView.vue`**:
  - `type FeedSource = 'public' | 'following' | 'for-you'`.
  - `TABS = [{ key: 'for-you', label: 'For You' }, { key: 'public', label: 'Latest' }, { key: 'following', label: 'Following' }]`.
  - Default tab becomes `for-you` (the `?tab=following` post-login bounce is unchanged; the else
    branch of `initialTab` changes from `'public'` to `'for-you'`).
  - `for-you` and `public` are open to anonymous users; only `following` keeps its login bounce in
    `selectTab`. Empty-state logic is unchanged — For You is backfilled, so it's only empty when the
    platform has no clips at all (generic "no clips yet" path).
  - `UnderlineTabs` already renders N tabs generically; no component change.

No new design tokens or layout work — three tabs fit the existing `UnderlineTabs`. Per project
convention, re-read `web/DESIGN.md` before touching the template.

## Testing (85/85 gate)

**Server integration** (`ClipsReadEndpointsTests`, mirroring existing feed tests):

- `source=for-you` anonymous → identical to global latest.
- Authenticated cold-start (no follows, no likes) → identical to global latest.
- Tier ordering: followed-author clip ranks above liked-game clip ranks above backfill, regardless
  of `createdAt`.
- Dedup: a followed author who posts in a liked game appears once, in tier 0.
- Cross-tier page fill: a `limit` that spans the tier-0/tier-1 (and tier-1/tier-2) boundary returns
  the right rows and a resumable cursor.
- Tiered-cursor round-trip: page 2 continues within the correct tier without repeats or skips;
  deep pagination drains and ends with `nextCursor: null`.
- Liked-game inference: liking a clip makes other clips in that game surface in tier 1.
- `likedByMe` is per-caller (not leaked via any shared state).

**Server unit** (`TieredKeysetCursor`): build→parse round-trip; corrupt/empty → `false` (tier-0
restart); a plain `KeysetCursor` token → `false`.

**Web** (`web/src/api/__tests__/clips.spec.ts`): `clips.feed({ source: 'for-you' })` serializes to
`?source=for-you`.

## Out of scope / follow-ups

- Engagement-weighted ranking within a tier (kept as pure recency for stable keyset pagination).
- Push/precomputed personalization; `EXISTS`-based signal checks for heavy-follow users.
- Any "Top Rated" tab — Trending already lives at its own `/trending` route; this issue only adds
  For You.
