<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { CLIPS, USERS, GAMES, formatNum, formatDuration, userByUsername } from '@/lib/mock-data'
import UserAvatar from '@/components/UserAvatar.vue'
import IconChevronRight from '@/components/icons/IconChevronRight.vue'

const router = useRouter()

const TIME_WINDOWS = [
  { key: '1h', label: 'Last hour' },
  { key: '24h', label: '24 hours' },
  { key: '7d', label: 'This week' },
  { key: '30d', label: 'This month' },
  { key: 'all', label: 'All time' },
] as const

const timeWindow = ref<string>('24h')

const topClips = computed(() => [...CLIPS].sort((a, b) => b.likes - a.likes).slice(0, 10))

function trendFor(i: number): 'up' | 'hold' | 'down' {
  if (i < 3) return 'up'
  if (i < 6) return 'hold'
  return i % 2 === 0 ? 'up' : 'down'
}

const TOP_CREATOR_KEYS = ['sundownr', 'phantomveil', 'nyxproto', 'rustyquill', 'wrenhowl'] as const
const CREATOR_GAINED = [3180, 2740, 1820, 990, 540]

function userKeyByUsername(username: string): string {
  return userByUsername(username)?.[0] ?? username
}

// Today's clip count per game, keyed by GAMES key (not by array index)
const HOT_GAMES_CLIPS_TODAY: Record<string, number> = {
  valorant: 412,
  rocket: 388,
  minecraft: 274,
  overwatch: 210,
  fortnite: 188,
  league: 156,
}
const DEFAULT_CLIPS_TODAY = 100
const gameEntries = Object.entries(GAMES)

// Simple deterministic sparkline points
function sparklinePoints(gameKey: string): string {
  const base = HOT_GAMES_CLIPS_TODAY[gameKey] ?? DEFAULT_CLIPS_TODAY
  const vals = [
    base * 0.55,
    base * 0.62,
    base * 0.48,
    base * 0.7,
    base * 0.65,
    base * 0.82,
    base * 0.91,
    base,
  ]
  const max = Math.max(...vals)
  const min = Math.min(...vals)
  const range = max - min || 1
  return vals
    .map((v, i) => {
      const x = (i / (vals.length - 1)) * 56
      const y = 12 - ((v - min) / range) * 12
      return `${x.toFixed(1)},${y.toFixed(1)}`
    })
    .join(' ')
}

const timeBtnBase =
  'px-3.5 py-1.5 rounded-sm font-mono text-[11px] uppercase tracking-[0.06em] border-none cursor-pointer'
const timeBtnActive = `${timeBtnBase} bg-brand text-white transition-[background] duration-150`
const timeBtnInactive = `${timeBtnBase} bg-transparent text-text-secondary transition-[color] duration-150`

const rankBase = 'font-heading font-bold text-[28px] leading-none'
const rankTop = `${rankBase} text-brand-light`
const rankRest = `${rankBase} text-text-muted`

const trendBase = 'font-mono text-[11px] leading-none'
const trendUp = `${trendBase} text-neon`
const trendDown = `${trendBase} text-error`
const trendHold = `${trendBase} text-text-muted`
</script>

<template>
  <main class="mx-auto max-w-360 px-6 pt-8 pb-30">
    <!-- Page header -->
    <div>
      <div class="mb-2 font-mono text-[11px] uppercase tracking-widest text-text-muted">
        Updated every 5 min · Ranked by likes + plays
      </div>
      <h1
        class="m-0 mb-5 font-heading text-[clamp(32px,4vw,52px)] font-bold uppercase leading-none tracking-[0.02em] text-text-primary"
      >
        Trending
      </h1>

      <!-- Time window toggle -->
      <div class="inline-flex gap-0.5 p-1 bg-surface-raised border border-border rounded-sm">
        <button
          v-for="tw in TIME_WINDOWS"
          :key="tw.key"
          :class="timeWindow === tw.key ? timeBtnActive : timeBtnInactive"
          @click="timeWindow = tw.key"
        >
          {{ tw.label }}
        </button>
      </div>
    </div>

    <!-- Two-column layout -->
    <div class="grid grid-cols-[minmax(0,1fr)_340px] gap-7 items-start max-[960px]:grid-cols-1">
      <!-- LEFT: Top 10 leaderboard -->
      <div class="bg-surface-raised border border-border rounded-md overflow-hidden mt-7">
        <div class="px-4 py-3.5 border-b border-border">
          <span class="font-heading font-bold text-sm uppercase text-text-secondary tracking-wider"
            >Top 10 this period</span
          >
        </div>

        <div
          v-for="(clip, i) in topClips"
          :key="clip.id"
          class="grid grid-cols-[60px_120px_1fr_auto_auto] gap-4 items-center px-4 py-3 cursor-pointer transition-[background] duration-150 border-b border-border last:border-b-0 hover:bg-surface-overlay"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        >
          <!-- Rank + trend -->
          <div class="flex flex-col items-start gap-0.5">
            <span :class="i < 3 ? rankTop : rankRest">#{{ i + 1 }}</span>
            <span
              :class="
                trendFor(i) === 'up' ? trendUp : trendFor(i) === 'down' ? trendDown : trendHold
              "
              >{{ trendFor(i) === 'up' ? '▲' : trendFor(i) === 'down' ? '▼' : '—' }}</span
            >
          </div>

          <!-- Thumbnail -->
          <div class="relative rounded-[4px] overflow-hidden aspect-video">
            <img :src="clip.art" alt="" class="w-full h-full object-cover block" />
            <span
              class="absolute bottom-1 right-1 font-mono text-[10px] text-white bg-black/75 px-1.25 py-0.5 rounded-[3px] leading-none"
              >{{ formatDuration(clip.duration) }}</span
            >
          </div>

          <!-- Title + meta -->
          <div class="min-w-0 flex flex-col gap-1">
            <span
              class="font-body text-[13px] font-medium text-text-primary leading-[1.35] line-clamp-2"
              >{{ clip.title }}</span
            >
            <div class="flex items-center gap-1.5 font-mono text-[10px]">
              <span
                class="bg-surface-base border border-border-strong rounded-[3px] px-1.5 py-0.5 text-text-secondary uppercase tracking-[0.06em]"
                >{{ GAMES[clip.game]?.tag }}</span
              >
              <span class="text-neon">@{{ USERS[clip.user]?.username }}</span>
            </div>
          </div>

          <!-- Stats -->
          <div
            class="flex flex-col gap-1 text-right font-mono text-[11px] text-text-secondary whitespace-nowrap"
          >
            <span>♥ {{ formatNum(clip.likes) }}</span>
            <span class="text-text-muted">{{ formatNum(clip.views) }} plays</span>
          </div>

          <!-- Chevron -->
          <div class="text-text-muted">
            <IconChevronRight :size="16" />
          </div>
        </div>
      </div>

      <!-- RIGHT sidebar -->
      <div class="flex flex-col gap-4 mt-7">
        <!-- Top creators -->
        <div class="bg-surface-raised border border-border rounded-md overflow-hidden">
          <div class="px-4 py-3.5 border-b border-border">
            <span
              class="section-title-bar flex items-center gap-2.5 font-heading font-bold text-sm uppercase text-text-secondary tracking-wider"
              >Top Creators</span
            >
          </div>

          <div
            v-for="(username, i) in TOP_CREATOR_KEYS"
            :key="username"
            class="flex items-center gap-3 px-4 py-2.5 border-b border-border last:border-b-0 cursor-pointer transition-[background] duration-150 hover:bg-surface-overlay"
            @click="router.push({ name: 'user', params: { username } })"
          >
            <span class="font-heading font-bold text-lg text-text-muted w-6 shrink-0 leading-none"
              >#{{ i + 1 }}</span
            >
            <UserAvatar :user="userKeyByUsername(username)" :size="36" />
            <div class="min-w-0 flex-1 flex flex-col gap-px">
              <span
                class="font-body text-[13px] font-medium text-text-primary whitespace-nowrap overflow-hidden text-ellipsis"
              >
                {{ USERS[userKeyByUsername(username)]?.display }}
              </span>
              <span class="font-mono text-[10px] text-text-muted"> @{{ username }} </span>
            </div>
            <span class="font-mono text-[10px] text-neon whitespace-nowrap shrink-0">
              +{{ formatNum(CREATOR_GAINED[i]) }}
            </span>
          </div>
        </div>

        <!-- Hot games -->
        <div class="bg-surface-raised border border-border rounded-md overflow-hidden">
          <div class="px-4 py-3.5 border-b border-border">
            <span
              class="section-title-bar flex items-center gap-2.5 font-heading font-bold text-sm uppercase text-text-secondary tracking-wider"
              >Hot Games</span
            >
          </div>

          <div
            v-for="[key, game] in gameEntries"
            :key="key"
            class="flex items-center gap-3 px-4 py-2.5 border-b border-border last:border-b-0 cursor-pointer transition-[background] duration-150 hover:bg-surface-overlay"
            @click="router.push({ name: 'games', query: { game: key } })"
          >
            <!-- Game art thumbnail -->
            <div class="w-10 h-10 rounded-[4px] overflow-hidden shrink-0 relative">
              <img :src="game.art" alt="" class="w-full h-full object-cover block" />
            </div>
            <div class="min-w-0 flex-1 flex flex-col gap-px">
              <span
                class="font-body text-[13px] font-medium text-text-primary whitespace-nowrap overflow-hidden text-ellipsis"
              >
                {{ game.name }}
              </span>
              <span class="font-mono text-[10px] text-neon">
                +{{ HOT_GAMES_CLIPS_TODAY[key] ?? DEFAULT_CLIPS_TODAY }} clips today
              </span>
            </div>
            <!-- Sparkline -->
            <svg width="56" height="14" viewBox="0 0 56 14" class="shrink-0 overflow-visible">
              <polyline
                :points="sparklinePoints(key)"
                fill="none"
                stroke="var(--color-neon)"
                stroke-width="1.5"
                stroke-linecap="round"
                stroke-linejoin="round"
                opacity="0.7"
              />
            </svg>
          </div>
        </div>
      </div>
    </div>
  </main>
</template>
