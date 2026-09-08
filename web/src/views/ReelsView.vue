<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, useTemplateRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { clips, type ClipDetail, type ClipFeedItem } from '@/api/clips'
import StatusPanel from '@/components/StatusPanel.vue'
import ReelClip from '@/components/reels/ReelClip.vue'

const route = useRoute()
const router = useRouter()

// --- State --------------------------------------------------------------------

const items = ref<ClipFeedItem[]>([])
const itemIds = reactive(new Set<string>())
const cursor = ref<string | null>(null)
const loading = ref(false)
const errored = ref(false)
const paginationErrored = ref(false)
const noMore = ref(false)

const details = reactive(new Map<string, ClipDetail>())
const detailErrors = reactive(new Set<string>())
const detailsInflight = new Set<string>()

const activeIndex = ref(0)
const globalMuted = ref(true)

// Race guard for paginated loads — late responses from an abandoned page can't
// stomp current state. Same pattern as ClipView.loadClip.
let latestLoadId = 0

const seedId = computed(() => {
  const id = route.params.id
  return Array.isArray(id) ? id[0] : (id as string | undefined)
})

// --- Helpers ------------------------------------------------------------------

// Project a ClipDetail (richer shape, includes videoUrl) into the ClipFeedItem
// shape used in items[]. Every field we render from items[] is a strict subset
// of detail — we drop videoUrl/width/height/visibility/videoUrlExpiresAt since
// the feed-item shape doesn't carry them.
function projectFeedItem(detail: ClipDetail): ClipFeedItem {
  return {
    id: detail.id,
    title: detail.title,
    description: detail.description,
    thumbnailUrl: detail.thumbnailUrl,
    durationSecs: detail.durationSecs,
    viewCount: detail.viewCount,
    likeCount: detail.likeCount,
    createdAt: detail.createdAt,
    author: detail.author,
    game: detail.game,
    tags: detail.tags,
    likedByMe: detail.likedByMe,
    shareCode: detail.shareCode,
  }
}

function appendItems(newItems: ClipFeedItem[]) {
  for (const c of newItems) {
    if (itemIds.has(c.id)) continue
    items.value.push(c)
    itemIds.add(c.id)
  }
}

// --- Initial load -------------------------------------------------------------

async function loadFirstPage() {
  loading.value = true
  errored.value = false
  paginationErrored.value = false
  const seed = seedId.value
  try {
    if (seed) {
      // Race detail + first page in parallel. If detail 404s, fall through to
      // the page-only behavior with a quiet console note (no toast for v1 —
      // the user's intent was to land on a clip that no longer exists; just
      // start from the top).
      const [seedDetail, page] = await Promise.allSettled([
        clips.getDetail(seed),
        clips.feed({ limit: 20 }),
      ])

      if (page.status === 'rejected') {
        throw page.reason
      }

      if (seedDetail.status === 'fulfilled') {
        const head = projectFeedItem(seedDetail.value)
        details.set(head.id, seedDetail.value)
        appendItems([head])
        appendItems(page.value.items)
      } else {
        // Seed 404 (or other failure) — start from the regular top of feed.
        const isMissing = seedDetail.reason instanceof ApiError && seedDetail.reason.status === 404
        if (!isMissing) console.error('reels: seed detail load failed', seedDetail.reason)
        appendItems(page.value.items)
        // Strip the bogus id from the URL so a subsequent reload doesn't keep
        // trying it. Use replace with the bare-route name and no params.
        selfNavigating = true
        await router.replace({ name: 'reels' })
        selfNavigating = false
      }

      cursor.value = page.value.nextCursor
      noMore.value = page.value.nextCursor === null
      activeIndex.value = 0
    } else {
      const page = await clips.feed({ limit: 20 })
      appendItems(page.items)
      cursor.value = page.nextCursor
      noMore.value = page.nextCursor === null
      activeIndex.value = 0
    }
  } catch (err) {
    console.error('reels: initial load failed', err)
    errored.value = true
  } finally {
    loading.value = false
  }
}

async function loadMore() {
  if (loading.value || noMore.value) return
  const myLoadId = ++latestLoadId
  loading.value = true
  paginationErrored.value = false
  try {
    const page = await clips.feed({ cursor: cursor.value, limit: 20 })
    if (myLoadId !== latestLoadId) return
    appendItems(page.items)
    cursor.value = page.nextCursor
    if (page.nextCursor === null) noMore.value = true
  } catch (err) {
    if (myLoadId !== latestLoadId) return
    console.error('reels: pagination failed', err)
    paginationErrored.value = true
  } finally {
    if (myLoadId === latestLoadId) loading.value = false
  }
}

// --- Detail prefetch (±1 window around activeIndex) ---------------------------

function prefetchDetailFor(id: string | undefined) {
  if (!id) return
  if (details.has(id) || detailsInflight.has(id) || detailErrors.has(id)) return
  detailsInflight.add(id)
  clips
    .getDetail(id)
    .then((d) => {
      details.set(id, d)
    })
    .catch((err) => {
      console.error('reels: detail prefetch failed', { id, err })
      detailErrors.add(id)
    })
    .finally(() => {
      detailsInflight.delete(id)
    })
}

function refreshPrefetchWindow() {
  const i = activeIndex.value
  const window = [items.value[i - 1]?.id, items.value[i]?.id, items.value[i + 1]?.id]
  for (const id of window) prefetchDetailFor(id)
}

function retryDetail(id: string) {
  detailErrors.delete(id)
  prefetchDetailFor(id)
}

watch(activeIndex, () => {
  refreshPrefetchWindow()
  // Pagination trigger — fire when we're within 3 of the end of the loaded list.
  if (
    !loading.value &&
    !noMore.value &&
    items.value.length > 0 &&
    activeIndex.value >= items.value.length - 3
  ) {
    loadMore()
  }
})

// Once items first arrive, kick the prefetch for index 0.
watch(
  () => items.value.length,
  (len, prev) => {
    if (prev === 0 && len > 0) refreshPrefetchWindow()
  },
)

// --- URL sync -----------------------------------------------------------------

let selfNavigating = false

// Watch the active clip's id (not the index) so the sync fires both when the
// user scrolls AND when items[] first populates after the initial load — in
// the bare-route case activeIndex stays 0 but items[0]?.id transitions from
// undefined → 'first', which an index-only watcher would miss.
watch(
  () => items.value[activeIndex.value]?.id,
  async (id) => {
    if (!id) return
    if (route.name === 'reel-clip' && route.params.id === id) return
    selfNavigating = true
    try {
      await router.replace({ name: 'reel-clip', params: { id } })
    } finally {
      selfNavigating = false
    }
  },
)

// Re-react to user-initiated route changes (e.g., browser back/forward).
// router.replace from our own URL-sync flips selfNavigating to suppress this.
watch(
  () => route.params.id,
  (newId) => {
    if (selfNavigating) return
    if (!newId || Array.isArray(newId)) return
    const idx = items.value.findIndex((c) => c.id === newId)
    if (idx >= 0 && idx !== activeIndex.value) {
      // Scroll the snap container to the target index; observer will follow.
      scrollToIndex(idx)
    }
  },
)

// --- Observer + scroll container ----------------------------------------------

const scrollEl = useTemplateRef<HTMLDivElement>('scrollEl')
const clipEls = new Map<string, HTMLElement>()
const elToId = new WeakMap<Element, string>()
const ratios = new Map<string, number>()
let observer: IntersectionObserver | null = null

function registerClip(id: string, el: Element | null) {
  // The callback ref signature gives us Element | null (unmount). Vue's
  // template-ref ergonomics use `null` on teardown.
  if (!el) {
    const prev = clipEls.get(id)
    if (prev) {
      observer?.unobserve(prev)
      elToId.delete(prev)
    }
    clipEls.delete(id)
    ratios.delete(id)
    return
  }
  const htmlEl = el as HTMLElement
  clipEls.set(id, htmlEl)
  elToId.set(htmlEl, id)
  observer?.observe(htmlEl)
}

function handleIntersect(entries: IntersectionObserverEntry[]) {
  for (const e of entries) {
    const id = elToId.get(e.target)
    if (!id) continue
    ratios.set(id, e.intersectionRatio)
  }
  // Pick the most-visible slot. Hysteresis: only switch if the new candidate
  // is clearly more visible (>= 0.6) AND differs from the current. Without
  // the gate, the moment two slots both sit at ~0.5 during a snap, activeIndex
  // would chatter and play/pause would thrash.
  let bestId: string | null = null
  let bestRatio = 0
  for (const [id, r] of ratios) {
    if (r > bestRatio) {
      bestRatio = r
      bestId = id
    }
  }
  if (bestRatio < 0.6 || !bestId) return
  const idx = items.value.findIndex((c) => c.id === bestId)
  if (idx >= 0 && idx !== activeIndex.value) activeIndex.value = idx
}

function scrollToIndex(idx: number) {
  const target = items.value[idx]
  if (!target) return
  const el = clipEls.get(target.id)
  if (!el || !scrollEl.value) return
  scrollEl.value.scrollTo({ top: el.offsetTop, behavior: 'smooth' })
}

onMounted(async () => {
  await loadFirstPage()
  // Initialize the observer after first paint so scrollEl is real.
  if (!scrollEl.value) return
  observer = new IntersectionObserver(handleIntersect, {
    root: scrollEl.value,
    threshold: [0, 0.5, 0.6, 0.75, 0.95],
  })
  // Observe any already-mounted children. Children mounted after observer
  // initialization get observed via registerClip directly.
  for (const el of clipEls.values()) observer.observe(el)
})

onBeforeUnmount(() => {
  if (observer) {
    observer.disconnect()
    observer = null
  }
})

// --- Interactions -------------------------------------------------------------

function onToggleMute() {
  globalMuted.value = !globalMuted.value
}

function onLikedChanged(payload: { id: string; liked: boolean; count: number }) {
  const idx = items.value.findIndex((c) => c.id === payload.id)
  if (idx < 0) return
  // Replace the item with a new object so reactivity propagates to child props
  // (mutating likedByMe in place would also work, but consistency wins).
  items.value[idx] = {
    ...items.value[idx],
    likedByMe: payload.liked,
    likeCount: payload.count,
  }
}
</script>

<template>
  <div class="fixed inset-x-0 top-14 bottom-15.5 z-10 flex justify-center bg-black lg:bottom-0">
    <!-- Initial load -->
    <StatusPanel
      v-if="loading && items.length === 0 && !errored"
      kind="loading"
      message="Loading reels"
    />

    <!-- Initial error -->
    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load reels.">
      <button
        type="button"
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="loadFirstPage"
      >
        Retry
      </button>
    </StatusPanel>

    <!-- Empty feed -->
    <StatusPanel
      v-else-if="!loading && items.length === 0"
      kind="empty"
      message="No clips yet. Be the first."
    >
      <RouterLink
        to="/upload"
        class="rounded-lg border border-border-strong px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
      >
        Upload a clip
      </RouterLink>
    </StatusPanel>

    <!-- Feed — vertical snap column; the full-screen viewport itself is the container. -->
    <div
      v-else
      ref="scrollEl"
      class="h-full w-full max-w-[min(448px,calc(100vh*9/16))] snap-y snap-mandatory overflow-y-scroll overscroll-contain scroll-smooth scrollbar-none [&::-webkit-scrollbar]:hidden"
      aria-label="Reels feed"
    >
      <div
        v-for="clip in items"
        :key="clip.id"
        :ref="(el) => registerClip(clip.id, el as HTMLElement | null)"
        class="h-full w-full snap-start snap-always"
      >
        <ReelClip
          :clip="clip"
          :detail="details.get(clip.id) ?? null"
          :detail-errored="detailErrors.has(clip.id)"
          :is-active="items[activeIndex]?.id === clip.id"
          :global-muted="globalMuted"
          @toggle-mute="onToggleMute"
          @retry-detail="retryDetail"
          @liked-changed="onLikedChanged"
        />
      </div>

      <!-- Pagination retry pill, snap-aligned at the bottom. -->
      <div
        v-if="paginationErrored"
        class="flex h-full w-full snap-start snap-always items-center justify-center"
      >
        <button
          type="button"
          class="cursor-pointer rounded-lg border border-white/20 bg-black/60 px-4 py-2 text-xs font-semibold text-[#f4f1e8] transition-colors duration-150 hover:border-accent hover:text-accent"
          @click="loadMore"
        >
          Couldn't load more. Retry
        </button>
      </div>
    </div>
  </div>
</template>
