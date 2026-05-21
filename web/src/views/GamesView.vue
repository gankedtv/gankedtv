<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { clips, type ClipFeedItem } from '@/api/clips'
import ClipCard from '@/components/ClipCard.vue'
import GameTag from '@/components/GameTag.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'

const router = useRouter()

const allGames = ref<GameListItem[]>([])
const allClips = ref<ClipFeedItem[]>([])
const loading = ref(false)
const errored = ref(false)

// Per-game clip counts are derived from the loaded feed page — fine as a rough
// indicator on the catalog tiles. The authoritative count for a game lives on
// the game-detail page (clipCount on GameDetail).
const clipCountByGame = computed(() => {
  const counts = new Map<string, number>()
  for (const c of allClips.value) {
    if (c.game) counts.set(c.game.slug, (counts.get(c.game.slug) ?? 0) + 1)
  }
  return counts
})

const featuredClips = computed(() => allClips.value.slice(0, 12))

async function load() {
  loading.value = true
  errored.value = false
  try {
    const [gs, feed] = await Promise.all([
      gamesApi.list(50, { hasClips: true }),
      clips.feed({ limit: 100 }),
    ])
    allGames.value = gs
    allClips.value = feed.items
  } catch {
    errored.value = true
  } finally {
    loading.value = false
  }
}

onMounted(load)

const tileBase =
  'min-h-27.5 block rounded-md border border-border bg-surface-raised p-4 text-left no-underline transition-[border-color,box-shadow] duration-150 hover:border-brand hover:shadow-[0_14px_40px_-14px_var(--color-brand-glow)]'
</script>

<template>
  <main class="mx-auto max-w-360 px-6 pt-8 pb-30">
    <PageHeader title="Games" pulse>
      <template #caption>
        Library · {{ allGames.length }} games · {{ allClips.length }} clips loaded
      </template>
      <p class="m-0 mt-2 max-w-[56ch] text-[15px] leading-normal text-text-secondary">
        Every clip is tagged with its game. Pick one to see all its clips.
      </p>
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load games.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="load"
      >
        Retry
      </button>
    </StatusPanel>

    <template v-else>
      <!-- Game tiles — each links to /game/:slug -->
      <div class="mt-8 grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-3.5">
        <RouterLink
          v-for="g in allGames"
          :key="g.id"
          :to="{ name: 'game-detail', params: { slug: g.slug } }"
          :class="[tileBase, 'relative overflow-hidden']"
        >
          <!-- Cover rendered as <img> (not background-image) so the URL can't break out of a
               CSS url() string — same treatment as the GameView header. Lazy-loaded because the
               catalog can grow to hundreds of tiles. -->
          <img
            v-if="g.coverUrl"
            :src="g.coverUrl"
            alt=""
            loading="lazy"
            decoding="async"
            class="absolute inset-0 h-full w-full object-cover opacity-25"
            aria-hidden="true"
          />
          <div
            class="absolute inset-0 bg-[linear-gradient(180deg,transparent_0%,var(--color-surface-raised)_100%)]"
            aria-hidden="true"
          ></div>
          <div class="relative flex h-full flex-col justify-between gap-2">
            <span class="font-heading text-xl font-bold leading-none uppercase text-text-primary">
              {{ g.name }}
            </span>
            <div class="flex items-center gap-2">
              <GameTag :tag="g.tag" tone="subtle" />
              <span class="font-mono text-[10px] tracking-[0.08em] text-text-muted">
                {{ clipCountByGame.get(g.slug) ?? 0 }} clips
              </span>
            </div>
          </div>
        </RouterLink>
      </div>

      <!-- Featured clips teaser -->
      <div class="mt-12">
        <div class="mb-5 flex items-baseline justify-between gap-4">
          <h2
            class="section-title-bar m-0 inline-flex items-center gap-3.5 font-heading text-2xl font-bold uppercase tracking-[0.02em] text-text-primary"
          >
            Featured across all games
          </h2>
        </div>

        <StatusPanel
          v-if="loading && featuredClips.length === 0"
          kind="loading"
          message="Loading…"
        />
        <div v-else-if="featuredClips.length" class="feed-grid">
          <ClipCard
            v-for="clip in featuredClips"
            :key="clip.id"
            :clip="clip"
            @click="router.push({ name: 'clip', params: { id: clip.id } })"
          />
        </div>
        <div
          v-else
          class="rounded-md border border-border bg-surface-raised p-8 text-center font-mono text-sm uppercase tracking-widest text-text-muted"
        >
          No clips yet.
        </div>
      </div>
    </template>
  </main>
</template>
