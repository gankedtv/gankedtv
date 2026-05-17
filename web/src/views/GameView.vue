<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { games, type GameDetail } from '@/api/games'
import type { ClipFeedItem } from '@/api/clips'
import { ApiError } from '@/api/client'
import ClipCard from '@/components/ClipCard.vue'
import GameTag from '@/components/GameTag.vue'
import PageHeader from '@/components/PageHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'

const route = useRoute()
const router = useRouter()

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

async function loadGame() {
  const s = slug.value
  if (!s) return
  try {
    game.value = await games.getBySlug(s)
  } catch (err) {
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
  loading.value = true
  paginationErrored.value = false
  try {
    const page = await games.clips(s, { cursor: cursor.value, limit: 20 })
    items.value.push(...page.items)
    cursor.value = page.nextCursor
    if (page.nextCursor === null) reachedEnd.value = true
  } catch (err) {
    if (items.value.length === 0) {
      // First-page failure (game lookup may have succeeded). Treat 404 here as
      // the same "game does not exist" branch so we don't render a broken header.
      if (err instanceof ApiError && err.status === 404) {
        notFound.value = true
      } else {
        errored.value = true
      }
    } else {
      paginationErrored.value = true
    }
    console.error('game-detail: load failed', err)
  } finally {
    loading.value = false
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
  errored.value = false
  notFound.value = false
  paginationErrored.value = false
  reachedEnd.value = false
  initialLoading.value = true
  game.value = null
  items.value = []
  cursor.value = null
  detachObserver()

  try {
    // Sequenced (not Promise.all) so that a failed game lookup short-circuits
    // before the clips fetch fires. Parallelising the two creates a race where
    // a 500 on /games/{slug} can set errored=true while /games/{slug}/clips
    // succeeds and silently populates items behind the error panel.
    await loadGame()
    if (notFound.value || errored.value) return
    await loadMore()
  } catch {
    // individual handlers already set the right flag
  } finally {
    initialLoading.value = false
    // Defer observer attachment to next tick so the sentinel is in the DOM
    // (it's only rendered when game is loaded and !errored).
    if (!notFound.value && !errored.value && !reachedEnd.value) {
      requestAnimationFrame(attachObserver)
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
  <main
    class="mx-auto max-w-360 px-6 pt-8 pb-30 max-[899px]:px-3.5 max-[899px]:pt-4 max-[899px]:pb-20"
  >
    <!-- Not-found state -->
    <StatusPanel v-if="notFound" kind="empty" message="No game with that slug.">
      <RouterLink
        to="/games"
        class="rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
      >
        Back to games
      </RouterLink>
    </StatusPanel>

    <!-- Initial error -->
    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load this game.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="retry"
      >
        Retry
      </button>
    </StatusPanel>

    <!-- Initial loading -->
    <StatusPanel v-else-if="initialLoading && !game" kind="loading" message="Loading…" />

    <template v-else-if="game">
      <!-- Header -->
      <section
        class="relative mb-10 overflow-hidden rounded-lg border border-border bg-surface-raised"
      >
        <div
          v-if="game.coverUrl"
          class="absolute inset-0 bg-cover bg-center opacity-30"
          :style="{ backgroundImage: `url(${game.coverUrl})` }"
          aria-hidden="true"
        ></div>
        <div
          class="absolute inset-0 bg-[linear-gradient(180deg,transparent_0%,var(--color-surface-raised)_100%)]"
          aria-hidden="true"
        ></div>
        <div class="relative px-8 py-10 max-[899px]:px-5 max-[899px]:py-7">
          <PageHeader :title="game.name" pulse>
            <template #caption>
              Game · {{ game.clipCount }} clip{{ game.clipCount === 1 ? '' : 's' }}
            </template>
            <div class="mt-3 flex items-center gap-3">
              <GameTag :tag="game.tag" size="md" />
              <span class="font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted">
                /{{ game.slug }}
              </span>
            </div>
          </PageHeader>
        </div>
      </section>

      <!-- Clip grid -->
      <div v-if="items.length" class="feed-grid">
        <ClipCard
          v-for="clip in items"
          :key="clip.id"
          :clip="clip"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        />
      </div>

      <!-- Empty -->
      <StatusPanel v-else-if="reachedEnd" kind="empty" message="No clips for this game yet.">
        <RouterLink
          to="/upload"
          class="rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        >
          Upload a clip
        </RouterLink>
      </StatusPanel>

      <!-- Sentinel (only when there's more to load) -->
      <div
        v-if="!reachedEnd"
        ref="sentinel"
        class="mt-8 flex items-center justify-center py-6"
        aria-hidden="true"
      >
        <span v-if="loading" class="font-mono text-[11px] uppercase tracking-widest text-text-muted"
          >Loading more…</span
        >
      </div>

      <!-- Pagination error (inline retry, keep loaded clips on screen) -->
      <div v-if="paginationErrored" class="mt-2 flex flex-col items-center gap-2">
        <span class="font-mono text-[11px] uppercase tracking-widest text-text-muted">
          Couldn't load more — try again.
        </span>
        <button
          :disabled="loading"
          @click="loadMore"
          class="cursor-pointer rounded-sm border border-border bg-surface-raised px-6 py-2.5 font-mono text-[11px] uppercase tracking-[0.08em] text-text-primary transition-colors duration-150 hover:border-brand-light disabled:opacity-50"
        >
          Retry
        </button>
      </div>
    </template>
  </main>
</template>
