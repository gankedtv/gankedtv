# Reels feed — design spec (issue #108)

Phase 4 (Scale & Grow) stretch item: a full-screen, vertical-swipe "reels" feed as an alternative way to browse clips. Web-only; reuses the existing `clips.feed({ cursor, limit, source })` API. No server changes.

## Scope

| Decision | Value |
|---|---|
| Frame strategy | Letterboxed, fits-width — clip at natural 16:9 inside a portrait-shaped column |
| Desktop layout | Phone-frame, centered (`min(420px, calc(90vh * 9/16))`); page bg shows on either side |
| Feed source | Public only, no toggle (mirror HomeView's tab UX deferred) |
| Player | Native `<video muted playsinline>` + custom overlay (not Plyr) |
| Deep-link | `/feed/reels/:id` — dedicated route; `router.replace` per active clip; no history entries per snap |
| Entry point | Floating action button on HomeView (bottom-right, position:fixed) |
| View tracking | 3-second accumulator, identical to `ClipView.vue` (`POST /clips/{id}/view`) |
| Overlay actions | Like, mute, comments (opens bottom sheet), author handle (→ `/user/:username`). Open-in-detail (→ `/clip/:id`) is reachable from the sheet header as a "View full clip →" link. |
| Out of scope (v1) | Keyboard nav, swipe gestures, share button, game-tag chip, preload tuning knob, virtualization. (Comments: bottom-sheet `CommentsSection` is in v1; truly inline comments — no sheet — are deferred.) |

## Files

**New**
- `web/src/views/ReelsView.vue` — route component, owns data + observer + URL sync
- `web/src/components/reels/ReelClip.vue` — single snap slot (thumbnail/video + overlay + view tracking)
- `web/src/components/reels/ReelsFab.vue` — FAB on HomeView
- `web/src/components/icons/IconVolumeMute.vue` — speaker-with-slash variant for the mute overlay
- `web/src/components/icons/IconMessageCircle.vue` — comments button glyph in the right-rail
- `web/src/components/icons/IconX.vue` — close affordance on the comments bottom sheet
- `web/src/components/icons/IconReels.vue` — FAB glyph on HomeView

**Edited**
- `web/src/router/index.ts` — two routes (`reels`, `reel-clip`) pointing at `ReelsView.vue`
- `web/src/views/HomeView.vue` — mount `<ReelsFab />` once

## Routing

```ts
{ path: '/feed/reels',     name: 'reels',     component: () => import('@/views/ReelsView.vue') },
{ path: '/feed/reels/:id', name: 'reel-clip', component: () => import('@/views/ReelsView.vue') },
```

Same component for both — `route.params.id` is `undefined` on the bare route, present on the deep-link route. The view does *not* re-fetch on route changes it triggered itself (`router.replace` for URL sync is gated with a `selfNavigating` flag).

## Data flow (`ReelsView`)

State:
```ts
const items = ref<ClipFeedItem[]>([])
const cursor = ref<string | null>(null)
const loading = ref(false)
const errored = ref(false)
const paginationErrored = ref(false)
const details = reactive(new Map<string, ClipDetail>())  // lazy videoUrl cache
const detailErrors = reactive(new Set<string>())         // failed prefetches (for retry)
const detailsInflight = new Set<string>()                // de-dup concurrent getDetail
const activeIndex = ref(0)
const globalMuted = ref(true)
```

Initial load:
- No seed id: `clips.feed({ limit: 20 })` → `items = page.items`
- With seed id: `Promise.all([clips.getDetail(seedId), clips.feed({ limit: 20 })])`. `seed` is projected to a `ClipFeedItem` shape and used as `items[0]`; the feed page is `.filter(c => c.id !== seedId)` so the seed isn't duplicated. `details.set(seedId, seed)` so the active clip has its video URL immediately.
- Failure on initial load → `errored = true`; full-page error panel with retry.

Pagination:
- `watch(activeIndex)` fires `loadMore()` when `activeIndex >= items.length - 3`.
- Uses a `latestLoadId` counter (same pattern as `ClipView.loadClip`) so a late response from an abandoned page can't stomp current state.
- Failure → `paginationErrored = true`; inline retry pill snap-aligned at the bottom of the feed.
- Cursor exhausted → no retry pill; user just stops snapping.

Detail prefetch:
- `watch(activeIndex)` computes `windowIds = [items[i-1], items[i], items[i+1]].map(c => c?.id)` (filtered for undefined at edges).
- For each id ∉ `details`, ∉ `detailsInflight`, ∉ `detailErrors` (don't infinite-retry), fire `clips.getDetail(id)` in the background. Success → `details.set(id, detail)`. Failure → `detailErrors.add(id)`; child shows a per-clip "couldn't load video — retry" overlay.

URL sync:
- `watch(activeIndex)` does `router.replace({ name: 'reel-clip', params: { id: items[activeIndex].id } })`.
- `selfNavigating = true` set before the replace; cleared on next route observation. The view's own params watcher early-returns when `selfNavigating === true`.

## Per-clip lifecycle

`ReelClip.vue` props/emits:
```ts
defineProps<{
  clip: ClipFeedItem
  detail: ClipDetail | null
  detailErrored: boolean
  isActive: boolean
  globalMuted: boolean
}>()
defineEmits<{
  (e: 'toggle-mute'): void
  (e: 'retry-detail', id: string): void
  (e: 'liked-changed', payload: { id: string; liked: boolean; count: number }): void
}>()
```

Render gates:
- `detail === null && !detailErrored` → thumbnail + delayed-fade spinner (250ms)
- `detailErrored` → thumbnail + "Couldn't load video. Retry." overlay → emits `retry-detail`
- `detail !== null` → `<video muted playsinline preload="metadata">` mounted; thumbnail hidden

`watch([() => props.isActive, () => props.detail, videoEl], ...)`:
- `active && detail && el` → set `el.muted = globalMuted`, `el.currentTime = 0`, `el.play().catch(needsTapToPlay = true)`; attach view tracking.
- `!active` → `el.pause()`, detach view tracking, reset `playedMs = 0`.

`watch(() => props.globalMuted, m => { if (videoEl.value) videoEl.value.muted = m })`.

View tracking: direct lift of `ClipView`'s pattern (per-tick `timeupdate` accumulator, clamped delta `[0, 1000ms]`, fire once at 3000ms via `clips.recordView(id)`, fire-and-forget). Scoped per `ReelClip` instance; remount re-arms (server-side 30-min dedup absorbs revisits).

## Observer (`ReelsView`)

Single `IntersectionObserver` on the scroll container:
```ts
new IntersectionObserver(handleIntersect, {
  root: scrollContainerEl.value,
  threshold: [0, 0.5, 0.75, 0.95],
})
```

Children register/unregister via a callback ref: `:ref="(el) => registerClip(clip.id, el)"`. Parent maintains `elToId: WeakMap<Element, string>` and `ratios: Map<string, number>`.

`handleIntersect`: merge entry ratios into `ratios`, then pick highest. Hysteresis gate: only update `activeIndex` if the new candidate's ratio `>= 0.6` *and* it differs from the current. This stops the brief two-clip overlap during a snap from chattering play/pause.

## Overlay UI

Right-rail column (TikTok convention) inside the portrait frame:
- Like: heart icon + count (formatNum). Optimistic flip, rolls back on error (lift `toggleLike` from ClipView). Anonymous tap → `router.push({ name: 'login', query: { redirect: '/feed/reels/' + clip.id } })`.
- Mute: speaker icon (or speaker-slash when muted). Emits `toggle-mute` → parent flips `globalMuted`.
- Comments: message-circle icon. Opens a `<Teleport>`-ed bottom sheet hosting `CommentsSection.vue`. Sheet header has a "View full clip →" link (open-in-detail) and an X close button. Dismiss on backdrop click or `Esc` (window-level listener).
- Author: avatar + `@username` → `/user/:username`.

Plus a top-left game/title strip (subtle) and a bottom safe-area for OS chrome on mobile.

## Error / empty / auth states

| State | UX |
|---|---|
| Initial load failed | Full-page `StatusPanel` (kind=error), "Retry" |
| Empty feed (no clips at all) | Full-page `StatusPanel` (kind=empty), "Upload a clip" CTA |
| Pagination failed | Snap-aligned bottom pill: "Couldn't load more. Retry." |
| Per-clip detail fetch failed | Per-slot overlay: "Couldn't load video. Retry." (other clips unaffected) |
| Autoplay rejected | "Tap to play" centered overlay (defensive; shouldn't fire with muted=true) |
| Deep-link clip 404 | Treat as "no seed", start at top of cursor feed; quiet toast: "That clip is no longer available" |
| Anonymous + like tap | Redirect to `/login?redirect=/feed/reels/:id` |

## FAB (`ReelsFab.vue`)

Position: `fixed`, `bottom: calc(env(safe-area-inset-bottom, 0px) + 24px)`, `right: 24px`, `z-50`. Circular, brand-filled, `IconReels` glyph. `RouterLink to="/feed/reels"`. No scroll-hiding behavior in v1.

Note on bottom positioning: we use `calc(safe-area + 24px)` rather than `max(safe-area, 24px)` so the FAB always sits 24px *above* the home indicator on devices with one, instead of resting directly on it.

Mounted once in `HomeView.vue` at the root template level, outside the `<main>` flow (so it doesn't shift page padding). Visible to authenticated and anonymous users alike.

## Testing

Coverage gate covers `api/`, `router/`, `stores/` only — reels code is in views/components and not in the denominator. Tests we write are for correctness, not coverage:

- `web/src/components/reels/__tests__/ReelClip.spec.ts` — render gates (thumb vs video), like toggle optimistic flow, mute toggle emits, anonymous like redirects to login.
- `web/src/views/__tests__/ReelsView.spec.ts` — dedup when seed id is in first page, URL replace on activeIndex change, pagination triggers at items.length - 3, deep-link 404 falls back to top of feed.
- `web/src/components/reels/__tests__/ReelsFab.spec.ts` — renders, links to `/feed/reels`.
- `web/src/router/__tests__/` — add assertions for the two new route name resolutions.

We mock `clips.feed`, `clips.getDetail`, `clips.recordView`, `clips.like`, `clips.unlike` directly. `IntersectionObserver` is stubbed on `globalThis` per test.

## What we explicitly defer

- Virtualization (DOM stays full for v1 — cursor pages of 20 mean ~5 pages = 100 nodes max, which is fine).
- Tab UX (public-only).
- Keyboard nav (J/K, arrows).
- Inline comments rendered directly in the overlay (deferred — the bottom-sheet `CommentsSection` covers the use case for v1).
- Share button (open-in-detail then share from there).
- Preload-window tuning knob (hardcoded ±1).
- Trending sort in reels.
