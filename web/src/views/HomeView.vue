<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { CLIPS, GAMES, USERS, formatNum, formatDuration } from '@/lib/mock-data'
import ClipCard from '@/components/ClipCard.vue'
import UserAvatar from '@/components/UserAvatar.vue'

const router = useRouter()

const hero = CLIPS[3] // Hanzo 6k
const heroUser = USERS[hero.user]
const heroGame = GAMES[hero.game]

const filter = ref<string>('all')
const sort = ref<string>('hot')

const GAME_FILTERS = [
  { key: 'all', label: 'All Games' },
  { key: 'valorant', label: 'Valorant' },
  { key: 'rocket', label: 'Rocket League' },
  { key: 'minecraft', label: 'Minecraft' },
  { key: 'overwatch', label: 'Overwatch 2' },
  { key: 'fortnite', label: 'Fortnite' },
  { key: 'league', label: 'LoL' },
]

const filteredClips = computed(() => {
  if (filter.value === 'all') return CLIPS
  return CLIPS.filter((c) => c.game === filter.value)
})

const secondaryClips = [CLIPS[0], CLIPS[5], CLIPS[1], CLIPS[11]]

const filterBase =
  'cursor-pointer rounded-full px-3 py-1.5 font-mono text-[11px] uppercase'
const filterActive = 'border border-text-primary bg-text-primary text-surface-base'
const filterInactive = 'border border-border bg-transparent text-text-muted'
</script>

<template>
  <main
    class="mx-auto max-w-360 px-6 pt-8 pb-30 max-[899px]:px-3.5 max-[899px]:pt-4 max-[899px]:pb-20"
  >
    <!-- Page header -->
    <div>
      <div
        class="mb-2 flex items-center gap-2 font-mono text-[11px] uppercase tracking-widest text-text-muted"
      >
        <span
          class="h-1.5 w-1.5 shrink-0 rounded-full bg-neon shadow-[0_0_8px_var(--color-neon)] animate-[pulse_2s_infinite]"
        ></span>
        Live Feed · {{ filteredClips.length }} clips
      </div>
      <h1
        class="m-0 font-heading text-[clamp(32px,4vw,52px)] font-bold leading-none uppercase tracking-[0.02em] text-text-primary"
      >
        The Feed
      </h1>
    </div>

    <!-- Desktop hero card -->
    <div
      class="relative mt-7 mb-12 hidden overflow-hidden rounded-lg border border-border bg-surface-raised min-[900px]:block"
    >
      <div class="grid min-h-115 grid-cols-[1.4fr_1fr]">
        <!-- Left: thumbnail -->
        <div class="relative overflow-hidden">
          <img :src="hero.art" alt="" class="block h-full w-full object-cover" />
          <!-- Fade overlay blending into right panel -->
          <div
            class="absolute inset-0 bg-[linear-gradient(90deg,transparent_50%,var(--color-surface-raised)_100%)]"
          ></div>
          <!-- Game badge -->
          <div class="absolute top-5 left-5">
            <span
              class="rounded-[3px] border border-border-strong bg-surface-base px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-primary"
            >
              {{ heroGame.tag }}
            </span>
          </div>
          <!-- Duration badge -->
          <div class="absolute bottom-5 left-5">
            <span
              class="rounded bg-black/70 px-2.5 py-1.25 font-mono text-[11px] tracking-[0.06em] text-white backdrop-blur-md"
            >
              {{ formatDuration(hero.duration) }}
            </span>
          </div>
          <!-- Play button overlay -->
          <button
            class="absolute inset-0 flex cursor-pointer items-center justify-center bg-transparent"
            @click="router.push({ name: 'clip', params: { id: hero.id } })"
          >
            <span
              class="inline-flex h-18 w-18 items-center justify-center rounded-full border border-white/20 bg-black/55 backdrop-blur-md"
            >
              <svg width="26" height="26" viewBox="0 0 24 24" fill="#fff">
                <path d="M8 5v14l11-7L8 5z" />
              </svg>
            </span>
          </button>
        </div>

        <!-- Right: content -->
        <div class="flex flex-col justify-between px-11 py-10">
          <!-- Top content -->
          <div class="flex flex-col gap-4">
            <div
              class="font-mono text-[11px] uppercase tracking-[0.15em] text-neon"
            >
              Featured Clip
            </div>
            <h2
              class="m-0 font-heading text-[46px] font-bold leading-none uppercase text-text-primary"
            >
              {{ hero.title }}
            </h2>
            <p class="m-0 max-w-[36ch] text-[15px] leading-normal text-text-secondary">
              {{ heroGame.name }} · uploaded {{ hero.createdAt }} ago by
              <span class="text-text-primary">@{{ heroUser.username }}</span>
            </p>
          </div>

          <!-- Stats row -->
          <div
            class="my-5 flex gap-7 border-y border-border py-4 font-mono"
          >
            <div class="flex flex-col gap-1">
              <span
                class="text-[10px] uppercase tracking-[0.08em] text-text-muted"
                >Views</span
              >
              <span
                class="font-heading text-[22px] font-bold text-text-primary"
                >{{ formatNum(hero.views) }}</span
              >
            </div>
            <div class="flex flex-col gap-1">
              <span
                class="text-[10px] uppercase tracking-[0.08em] text-text-muted"
                >Likes</span
              >
              <span
                class="font-heading text-[22px] font-bold text-text-primary"
                >{{ formatNum(hero.likes) }}</span
              >
            </div>
            <div class="flex flex-col gap-1">
              <span
                class="text-[10px] uppercase tracking-[0.08em] text-text-muted"
                >Duration</span
              >
              <span
                class="font-heading text-[22px] font-bold text-text-primary"
                >{{ formatDuration(hero.duration) }}</span
              >
            </div>
          </div>

          <!-- CTA row -->
          <div class="flex items-center gap-3">
            <button
              class="cursor-pointer rounded-sm border-none bg-brand px-6 py-2.5 font-mono text-[11px] uppercase tracking-[0.12em] text-white transition-colors duration-150 hover:bg-brand-light"
              @click="router.push({ name: 'clip', params: { id: hero.id } })"
            >
              Watch Now
            </button>
            <!-- Author pill -->
            <button
              class="flex cursor-pointer items-center gap-2 rounded-full border border-border bg-surface-overlay py-2 pr-3.5 pl-2 transition-colors duration-150 hover:border-border-hover"
              @click="router.push({ name: 'user', params: { username: heroUser.username } })"
            >
              <UserAvatar :user="hero.user" :size="28" />
              <span
                class="font-mono text-[11px] tracking-[0.04em] text-text-secondary"
                >@{{ heroUser.username }}</span
              >
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- Feed toolbar -->
    <div
      class="mb-7 flex flex-wrap items-center gap-3 border-b border-border py-3"
    >
      <button
        v-for="f in GAME_FILTERS"
        :key="f.key"
        :class="[filterBase, filter === f.key ? filterActive : filterInactive]"
        @click="filter = f.key"
      >
        {{ f.label }}
      </button>

      <!-- Sort -->
      <div
        class="ml-auto flex items-center gap-2 font-mono text-[11px] uppercase text-text-muted"
      >
        <span>Sort:</span>
        <select
          v-model="sort"
          class="cursor-pointer rounded-sm border border-border bg-surface-raised px-2.5 py-1.5 font-mono text-[11px] text-text-primary outline-none"
        >
          <option value="hot">Hot</option>
          <option value="new">New</option>
          <option value="top">Top</option>
        </select>
      </div>
    </div>

    <!-- Feed grid -->
    <div class="feed-grid">
      <ClipCard
        v-for="clip in filteredClips"
        :key="clip.id"
        :clip="clip"
        @click="router.push({ name: 'clip', params: { id: clip.id } })"
      />
    </div>

    <!-- Rising in your games -->
    <div>
      <div class="mt-12 mb-5 flex items-baseline justify-between gap-4">
        <h2
          class="section-title-bar m-0 flex items-center gap-3.5 font-heading text-2xl font-bold uppercase text-text-primary"
        >
          Rising in Your Games
        </h2>
        <a
          href="#"
          class="font-mono text-[11px] uppercase tracking-[0.06em] whitespace-nowrap text-text-secondary"
          >See All →</a
        >
      </div>

      <div class="feed-grid">
        <ClipCard
          v-for="clip in secondaryClips"
          :key="clip.id"
          :clip="clip"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        />
      </div>
    </div>
  </main>
</template>
