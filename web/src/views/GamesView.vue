<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { clips, type ClipFeedItem } from '@/api/clips'
import ClipCard from '@/components/ClipCard.vue'

const router = useRouter()

const allGames = ref<GameListItem[]>([])
const allClips = ref<ClipFeedItem[]>([])
const loading = ref(false)
const errored = ref(false)

// 'all' shows the entire feed grid; selecting a game filters client-side off the
// loaded feed page. Per-game endpoints with their own pagination are out of scope
// for this PR — see the backlog for `GET /games/{slug}/clips`.
const active = ref<'all' | string>('all')

const clipCountByGame = computed(() => {
  const counts = new Map<string, number>()
  for (const c of allClips.value) {
    if (c.game) counts.set(c.game.slug, (counts.get(c.game.slug) ?? 0) + 1)
  }
  return counts
})

const filteredClips = computed(() => {
  if (active.value === 'all') return allClips.value.slice(0, 12)
  return allClips.value.filter((c) => c.game?.slug === active.value).slice(0, 12)
})

const sectionTitle = computed(() => {
  if (active.value === 'all') return 'Featured across all games'
  const g = allGames.value.find((x) => x.slug === active.value)
  return g ? `Top in ${g.name}` : 'Top clips'
})

async function load() {
  loading.value = true
  errored.value = false
  try {
    const [gs, feed] = await Promise.all([gamesApi.list(50), clips.feed({ limit: 100 })])
    allGames.value = gs
    allClips.value = feed.items
  } catch {
    errored.value = true
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  // Allow deep links like /games?game=valorant from TrendingView.
  const initial = router.currentRoute.value.query.game
  if (typeof initial === 'string' && initial) active.value = initial
  load()
})

// Vue-router reuses the GamesView component on in-app navigations between
// /games?game=X URLs, so onMounted only runs once. Watch the query so a
// router.push to a different game keeps the picker in sync.
watch(
  () => router.currentRoute.value.query.game,
  (next) => {
    if (typeof next === 'string' && next && next !== active.value) {
      active.value = next
    }
  },
)

const tileBase =
  'min-h-27.5 cursor-pointer rounded-md border p-4 text-left transition-[border-color,box-shadow] duration-150 hover:border-border-hover'
const tileInactive = 'border-border bg-surface-raised'
const tileActive = 'border-brand shadow-[0_14px_40px_-14px_var(--color-brand-glow)]'
const tileActiveAll = `${tileActive} bg-brand`
</script>

<template>
  <main class="mx-auto max-w-360 px-6 pt-8 pb-30">
    <div>
      <div
        class="mb-2 flex items-center gap-2 font-mono text-[11px] uppercase tracking-widest text-text-muted"
      >
        <span
          class="block h-1.5 w-1.5 shrink-0 rounded-full bg-neon shadow-[0_0_8px_var(--color-neon)] animate-[pulse_2s_infinite]"
        ></span>
        Library · {{ allGames.length }} games · {{ allClips.length }} clips loaded
      </div>
      <h1
        class="m-0 mb-2 font-heading text-[clamp(32px,4vw,52px)] font-bold leading-none uppercase tracking-[0.02em] text-text-primary"
      >
        Games
      </h1>
      <p class="m-0 max-w-[56ch] text-[15px] leading-normal text-text-secondary">
        Every clip is tagged with its game. Pick a game to filter the feed.
      </p>
    </div>

    <div
      v-if="errored"
      class="mt-10 flex flex-col items-center gap-2 rounded-md border border-border bg-surface-raised py-12"
    >
      <span class="font-mono text-sm uppercase tracking-widest text-text-muted">
        Couldn't load games.
      </span>
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="load"
      >
        Retry
      </button>
    </div>

    <template v-else>
      <!-- Game tiles -->
      <div class="mt-8 grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-3.5">
        <button
          :class="[tileBase, active === 'all' ? tileActiveAll : tileInactive]"
          @click="active = 'all'"
        >
          <div class="flex h-full flex-col justify-between gap-2">
            <span class="font-heading text-xl font-bold leading-none uppercase text-white">
              All Games
            </span>
            <div class="flex flex-col gap-0.75">
              <span class="font-mono text-[10px] tracking-[0.08em] text-neon">
                {{ allClips.length }} clips
              </span>
            </div>
          </div>
        </button>

        <button
          v-for="g in allGames"
          :key="g.id"
          :class="[
            tileBase,
            'relative overflow-hidden',
            active === g.slug ? tileActive : 'border-border bg-surface-raised',
          ]"
          @click="active = g.slug"
        >
          <div class="relative flex h-full flex-col justify-between gap-2">
            <span class="font-heading text-xl font-bold leading-none uppercase text-text-primary">
              {{ g.name }}
            </span>
            <div class="flex items-center gap-2">
              <span
                class="rounded-[3px] border border-border-strong bg-surface-base px-1.5 py-0.5 font-mono text-[10px] uppercase tracking-[0.06em] text-text-secondary"
              >
                {{ g.tag }}
              </span>
              <span class="font-mono text-[10px] tracking-[0.08em] text-text-muted">
                {{ clipCountByGame.get(g.slug) ?? 0 }} clips
              </span>
            </div>
          </div>
        </button>
      </div>

      <!-- Clip section -->
      <div class="mt-12">
        <div class="mb-5 flex items-baseline justify-between gap-4">
          <h2
            class="section-title-bar m-0 inline-flex items-center gap-3.5 font-heading text-2xl font-bold uppercase tracking-[0.02em] text-text-primary"
          >
            {{ sectionTitle }}
          </h2>
        </div>

        <div v-if="loading && filteredClips.length === 0" class="py-12 text-center">
          <span class="font-mono text-sm uppercase tracking-widest text-text-muted">Loading…</span>
        </div>
        <div v-else-if="filteredClips.length" class="feed-grid">
          <ClipCard
            v-for="clip in filteredClips"
            :key="clip.id"
            :clip="clip"
            @click="router.push({ name: 'clip', params: { id: clip.id } })"
          />
        </div>
        <div
          v-else
          class="rounded-md border border-border bg-surface-raised p-8 text-center font-mono text-sm uppercase tracking-widest text-text-muted"
        >
          {{ active === 'all' ? 'No clips yet.' : 'No recent clips for this game.' }}
        </div>
      </div>
    </template>
  </main>
</template>
