<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { games, type GameDetail } from '@/api/games'
import type { ClipFeedItem } from '@/api/clips'
import { ApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import ClipCard from '@/components/ClipCard.vue'
import GameTag from '@/components/GameTag.vue'
import GameLeaderboardBlock from '@/components/GameLeaderboardBlock.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const slug = computed(() => {
  const raw = route.params.slug
  return Array.isArray(raw) ? raw[0] : raw
})

const game = ref<GameDetail | null>(null)
const items = ref<ClipFeedItem[]>([])
const cursor = ref<string | null>(null)
// Hit-end flag: distinguishes "more pages exist" (cursor !== null) from "first
// page returned an empty list but the game does exist". Needed so the observer
// doesn't keep firing forever and so the empty-state copy renders correctly.
const reachedEnd = ref(false)
const loading = ref(false)
const initialLoading = ref(true)
const errored = ref(false)
const notFound = ref(false)
const paginationErrored = ref(false)

const sentinel = ref<HTMLElement | null>(null)
let observer: IntersectionObserver | null = null

// Monotonic request token. Bumped at the start of every loadAll (i.e. on mount
// and every slug change). Each in-flight fetch captures the token at entry and
// bails when it resolves if a newer loadAll has superseded it — otherwise a
// stale response for slug A would stomp state belonging to slug B after a fast
// nav. Also gates loading.value clear-down so a stale finally doesn't flip
// loading off mid-load for the current request.
let requestId = 0

async function loadGame() {
  const s = slug.value
  if (!s) return
  const token = requestId
  try {
    const result = await games.getBySlug(s)
    if (token !== requestId) return
    game.value = result
  } catch (err) {
    if (token !== requestId) return
    if (err instanceof ApiError && err.status === 404) {
      notFound.value = true
    } else {
      errored.value = true
    }
    throw err
  }
}

async function loadMore() {
  const s = slug.value
  if (!s || loading.value || reachedEnd.value || notFound.value) return
  const token = requestId
  loading.value = true
  paginationErrored.value = false
  try {
    // No explicit limit — server defaults match the global feed (FeedDefaultLimit=20)
    // so we don't fork the value across client + server.
    const page = await games.clips(s, { cursor: cursor.value })
    if (token !== requestId) return
    items.value.push(...page.items)
    cursor.value = page.nextCursor
    if (page.nextCursor === null) reachedEnd.value = true
  } catch (err) {
    if (token !== requestId) return
    if (items.value.length === 0) {
      // First-page failure (game lookup may have succeeded). Treat 404 here as
      // the same "game does not exist" branch so we don't render a broken header.
      if (err instanceof ApiError && err.status === 404) {
        notFound.value = true
      } else {
        errored.value = true
      }
    } else {
      // Detach the observer so micro-scrolls past the sentinel don't silently
      // hammer the failing endpoint. The Retry button is the only way out — it
      // clears paginationErrored and re-attaches the observer.
      paginationErrored.value = true
      detachObserver()
    }
    console.error('game-detail: load failed', err)
  } finally {
    if (token === requestId) loading.value = false
  }
}

async function retryLoadMore() {
  await loadMore()
  if (!paginationErrored.value && !reachedEnd.value) {
    attachObserver()
  }
}

function attachObserver() {
  if (observer || !sentinel.value || reachedEnd.value) return
  observer = new IntersectionObserver(
    (entries) => {
      if (entries.some((e) => e.isIntersecting)) loadMore()
    },
    // rootMargin pre-fetches the next page ~400px before the sentinel actually
    // enters the viewport so the user rarely sees a loading gap.
    { rootMargin: '400px' },
  )
  observer.observe(sentinel.value)
}

function detachObserver() {
  observer?.disconnect()
  observer = null
}

async function loadAll() {
  const token = ++requestId
  errored.value = false
  notFound.value = false
  paginationErrored.value = false
  reachedEnd.value = false
  initialLoading.value = true
  game.value = null
  items.value = []
  cursor.value = null
  // Clear here too: an in-flight loadMore for the previous slug may still own
  // loading=true, and its finally won't clear it (token mismatch). Without this
  // reset, the new slug's loadMore would early-return on `loading.value` and
  // wedge the page in a permanent loading state.
  loading.value = false
  detachObserver()

  try {
    // Sequenced (not Promise.all) so that a failed game lookup short-circuits
    // before the clips fetch fires. Parallelising the two creates a race where
    // a 500 on /games/{slug} can set errored=true while /games/{slug}/clips
    // succeeds and silently populates items behind the error panel.
    await loadGame()
    if (token !== requestId) return
    if (notFound.value || errored.value) return
    await loadMore()
  } catch {
    // individual handlers already set the right flag
  } finally {
    // Bare `return` in finally is unsafe (masks throws), so gate via if-block.
    if (token === requestId) {
      initialLoading.value = false
      // Defer observer attachment to next tick so the sentinel is in the DOM
      // (it's only rendered when game is loaded and !errored).
      if (!notFound.value && !errored.value && !reachedEnd.value) {
        requestAnimationFrame(attachObserver)
      }
    }
  }
}

function retry() {
  loadAll()
}

onMounted(loadAll)
onBeforeUnmount(detachObserver)

// Client-side nav between two /game/:slug URLs must reset state and reload.
watch(slug, () => {
  loadAll()
})
</script>

<template>
  <main class="mx-auto max-w-360 px-8 pt-10 pb-30 max-tablet:px-4 max-tablet:pt-5 max-tablet:pb-20">
    <!-- Not-found state -->
    <StatusPanel v-if="notFound" kind="empty" message="No game with that slug.">
      <RouterLink
        to="/games"
        class="border border-border px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
      >
        Back to games
      </RouterLink>
    </StatusPanel>

    <!-- Initial error -->
    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load this game.">
      <button
        class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
        @click="retry"
      >
        Retry
      </button>
    </StatusPanel>

    <!-- Initial loading -->
    <StatusPanel v-else-if="initialLoading && !game" kind="loading" message="Loading" />

    <template v-else-if="game">
      <!-- Editorial header: 3:4 cover tile + kicker + oversized name + meta. -->
      <section class="mb-10 flex items-end gap-7 border-b border-border pb-7 max-tablet:flex-col max-tablet:items-start max-tablet:gap-4">
        <!-- Crisp portrait cover (real box-art aspect, no crop). alt="" — decorative: the game
             name is the visible <h1> right beside it, so a bound alt would re-announce it.
             Covers are <img> not background-image so a hostile coverUrl can't break out of a
             CSS url() string. -->
        <img
          v-if="game.coverUrl"
          :src="game.coverUrl"
          alt=""
          class="aspect-3/4 w-30 shrink-0 border border-border object-cover max-tablet:w-24"
        />
        <div class="min-w-0">
          <p class="m-0 font-mono text-[10px] uppercase tracking-[0.22em] text-text-secondary">
            <span class="text-ink">The Catalogue</span> · {{ game.clipCount }} clip{{
              game.clipCount === 1 ? '' : 's'
            }}
            filed
          </p>
          <h1
            class="m-0 mt-2 font-heading text-[clamp(36px,4.5vw,52px)] font-bold uppercase leading-none tracking-[0.01em] text-text-primary"
          >
            {{ game.name }}
          </h1>
          <div class="mt-3 flex items-center gap-3">
            <GameTag :tag="game.tag" size="md" />
            <span class="font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted">
              /{{ game.slug }}
            </span>
          </div>
        </div>
      </section>

      <!-- Top this week — block self-hides when the game has no likes in the window
           so empty games don't carry a phantom leaderboard header. -->
      <GameLeaderboardBlock :slug="game.slug" window="week" :limit="5" />

      <!-- Clip grid -->
      <section v-if="items.length">
        <SectionHeader roman="III" kicker="Latest" />
        <div class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-x-5.5 gap-y-7 pt-6">
          <ClipCard
            v-for="clip in items"
            :key="clip.id"
            :clip="clip"
            @click="router.push({ name: 'clip', params: { id: clip.id } })"
          />
        </div>
      </section>

      <!-- Empty. CTA only shown to authenticated users — /upload requires auth
           and would otherwise bounce visitors through login. -->
      <StatusPanel v-else-if="reachedEnd" kind="empty" message="No clips for this game yet.">
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="border border-border px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
        >
          Upload a clip
        </RouterLink>
      </StatusPanel>

      <!-- Sentinel: an empty observation target. aria-hidden so screen readers
           skip the layout div; the live-region below carries the loading text. -->
      <div v-if="!reachedEnd" ref="sentinel" class="mt-8 py-6" aria-hidden="true"></div>
      <div
        v-if="loading && !reachedEnd"
        role="status"
        aria-live="polite"
        class="-mt-6 flex items-center justify-center py-3 font-mono text-[11px] uppercase tracking-widest text-text-muted"
      >
        Loading more…
      </div>

      <!-- Pagination error (inline retry, keep loaded clips on screen). The
           observer is detached when paginationErrored flips, so the only way
           back to loading is this button — retryLoadMore re-attaches on success. -->
      <div v-if="paginationErrored" class="mt-2 flex flex-col items-center gap-2">
        <span class="font-mono text-[11px] uppercase tracking-widest text-text-muted">
          Couldn't load more — try again.
        </span>
        <button
          :disabled="loading"
          @click="retryLoadMore"
          class="cursor-pointer border border-border bg-transparent px-6 py-2.5 font-mono text-[11px] uppercase tracking-[0.08em] text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink disabled:opacity-50"
        >
          Retry
        </button>
      </div>
    </template>
  </main>
</template>
