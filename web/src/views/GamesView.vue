<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { GAMES, CLIPS } from '@/lib/mock-data'
import ClipCard from '@/components/ClipCard.vue'

const router = useRouter()

const active = ref<'all' | string>('all')

const gameCount = Object.keys(GAMES).length
const clipCount = CLIPS.length

const creatorCount = new Set(CLIPS.map((c) => c.user)).size

function gameClipCount(key: string) {
  return CLIPS.filter((c) => c.game === key).length
}
function gameCreatorCount(key: string) {
  return new Set(CLIPS.filter((c) => c.game === key).map((c) => c.user)).size
}

const filteredClips = computed(() => {
  const base = active.value === 'all' ? CLIPS : CLIPS.filter((c) => c.game === active.value)
  if (base.length === 0) return []
  const result: typeof CLIPS = []
  while (result.length < 8) {
    result.push(...base)
  }
  return result.slice(0, 8)
})

const sectionTitle = computed(() =>
  active.value === 'all' ? 'Featured across all games' : `Top in ${GAMES[active.value]?.name}`,
)

const tileBase =
  'min-h-27.5 cursor-pointer rounded-md border p-4 text-left transition-[border-color,box-shadow] duration-150 hover:border-border-hover'
const tileInactive = 'border-border bg-surface-raised'
const tileActive = 'border-brand shadow-[0_14px_40px_-14px_var(--color-brand-glow)]'
const tileActiveAll = `${tileActive} bg-brand`
</script>

<template>
  <main class="mx-auto max-w-360 px-6 pt-8 pb-30">
    <!-- Page header -->
    <div>
      <div
        class="mb-2 flex items-center gap-2 font-mono text-[11px] uppercase tracking-widest text-text-muted"
      >
        <span
          class="block h-1.5 w-1.5 shrink-0 rounded-full bg-neon shadow-[0_0_8px_var(--color-neon)] animate-[pulse_2s_infinite]"
        ></span>
        Library · {{ gameCount }} games · {{ clipCount * 200 }}+ clips indexed
      </div>
      <h1
        class="m-0 mb-2 font-heading text-[clamp(32px,4vw,52px)] font-bold leading-none uppercase tracking-[0.02em] text-text-primary"
      >
        Games
      </h1>
      <p class="m-0 max-w-[56ch] text-[15px] leading-normal text-text-secondary">
        Every clip is tagged with its game. Pick a game to see its feed, top creators, and today's
        highlights.
      </p>
    </div>

    <!-- Game tiles -->
    <div class="mt-8 grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-3.5">
      <!-- All games tile -->
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
              {{ clipCount * 200 }}+ clips
            </span>
            <span class="font-mono text-[10px] tracking-[0.08em] text-white/70">
              {{ creatorCount }} creators
            </span>
          </div>
        </div>
      </button>

      <!-- Per-game tiles -->
      <button
        v-for="(game, key) in GAMES"
        :key="key"
        :class="[
          tileBase,
          'relative overflow-hidden',
          active === key ? tileActive : 'border-border bg-surface-raised',
        ]"
        @click="active = key"
      >
        <!-- Background art -->
        <img
          :src="game.art"
          alt=""
          class="absolute inset-0 h-full w-full object-cover opacity-40"
        />
        <!-- Gradient overlay -->
        <div
          class="absolute inset-0 bg-[linear-gradient(160deg,rgba(8,8,16,0.4)_0%,rgba(8,8,16,0.85)_100%)]"
        ></div>
        <!-- Content -->
        <div class="relative flex h-full flex-col justify-between gap-2">
          <span class="font-heading text-xl font-bold leading-none uppercase text-white">
            {{ game.name }}
          </span>
          <div class="flex flex-col gap-0.75">
            <span class="font-mono text-[10px] tracking-[0.08em] text-neon">
              {{ gameClipCount(key) * 200 }}+ clips
            </span>
            <span class="font-mono text-[10px] tracking-[0.08em] text-white/70">
              {{ gameCreatorCount(key) }} creators
            </span>
          </div>
        </div>
      </button>
    </div>

    <!-- Clip section -->
    <div class="mt-12">
      <!-- Section header -->
      <div class="mb-5 flex items-baseline justify-between gap-4">
        <h2
          class="section-title-bar m-0 inline-flex items-center gap-3.5 font-heading text-2xl font-bold uppercase tracking-[0.02em] text-text-primary"
        >
          {{ sectionTitle }}
        </h2>
        <a
          href="#"
          class="font-mono text-[11px] uppercase tracking-[0.06em] text-text-secondary whitespace-nowrap"
        >
          See all ·→
        </a>
      </div>

      <!-- Clip grid -->
      <div class="feed-grid">
        <ClipCard
          v-for="(clip, i) in filteredClips"
          :key="`${clip.id}-${i}`"
          :clip="clip"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        />
      </div>
    </div>
  </main>
</template>
