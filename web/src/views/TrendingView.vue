<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { clips, type ClipFeedItem } from '@/api/clips'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { formatNum } from '@/lib/format'
import GameTag from '@/components/GameTag.vue'
import DurationBadge from '@/components/DurationBadge.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import IconChevronRight from '@/components/icons/IconChevronRight.vue'

const router = useRouter()

const TIME_WINDOWS = [
  { key: '1h', label: 'Last hour' },
  { key: '24h', label: '24 hours' },
  { key: '7d', label: 'This week' },
  { key: '30d', label: 'This month' },
  { key: 'all', label: 'All time' },
] as const

// The server doesn't filter by time window yet — these tabs are visual until that
// lands. Surfacing them keeps the UX intent visible (and lets the server-side
// follow-up just wire `?since=` without UI work).
const timeWindow = ref<string>('24h')

const allClips = ref<ClipFeedItem[]>([])
const loading = ref(false)
const errored = ref(false)

const topClips = computed(() =>
  [...allClips.value].sort((a, b) => b.likeCount - a.likeCount).slice(0, 10),
)

const hotGames = ref<GameListItem[]>([])

async function load() {
  loading.value = true
  errored.value = false
  try {
    const [feed, games] = await Promise.all([clips.feed({ limit: 100 }), gamesApi.list(8)])
    allClips.value = feed.items
    hotGames.value = games
  } catch (err) {
    console.error('trending: load failed', err)
    errored.value = true
  } finally {
    loading.value = false
  }
}

onMounted(load)

// Visual-only indicator on the leaderboard rows — *not* derived from real
// trend data. We don't track engagement deltas yet (server has no time-window
// query). Top 3 always show ▲, 3–5 show —, the rest alternate. Replace once a
// real `trendDelta` field lands on the trending response.
function trendFor(i: number): 'up' | 'hold' | 'down' {
  if (i < 3) return 'up'
  if (i < 6) return 'hold'
  return i % 2 === 0 ? 'up' : 'down'
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
        Ranked by likes (server-side trending coming soon)
      </div>
      <h1
        class="m-0 mb-5 font-heading text-[clamp(32px,4vw,52px)] font-bold uppercase leading-none tracking-[0.02em] text-text-primary"
      >
        Trending
      </h1>

      <!-- Time window toggle. Server doesn't support `?since=` yet — non-active
           tabs are disabled until the backend filter lands so the controls don't
           lie about what they do. -->
      <div class="inline-flex gap-0.5 p-1 bg-surface-raised border border-border rounded-sm">
        <button
          v-for="tw in TIME_WINDOWS"
          :key="tw.key"
          :class="[
            timeWindow === tw.key ? timeBtnActive : timeBtnInactive,
            tw.key === timeWindow ? '' : 'opacity-50 cursor-not-allowed',
          ]"
          :disabled="tw.key !== timeWindow"
          :title="tw.key === timeWindow ? '' : 'Server-side time filtering coming soon'"
        >
          {{ tw.label }}
        </button>
      </div>
    </div>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load trending.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="load"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel
      v-else-if="!loading && topClips.length === 0"
      kind="empty"
      message="No clips yet — be the first."
    />

    <!-- Two-column layout -->
    <div
      v-else
      class="grid grid-cols-[minmax(0,1fr)_340px] gap-7 items-start max-[960px]:grid-cols-1"
    >
      <!-- LEFT: Top 10 leaderboard -->
      <div class="bg-surface-raised border border-border rounded-md overflow-hidden mt-7">
        <div class="px-4 py-3.5 border-b border-border">
          <span class="font-heading font-bold text-sm uppercase text-text-secondary tracking-wider"
            >Top 10 by likes</span
          >
        </div>

        <div
          v-for="(clip, i) in topClips"
          :key="clip.id"
          class="grid grid-cols-[60px_120px_1fr_auto_auto] gap-4 items-center px-4 py-3 cursor-pointer transition-[background] duration-150 border-b border-border last:border-b-0 hover:bg-surface-overlay"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        >
          <div class="flex flex-col items-start gap-0.5">
            <span :class="i < 3 ? rankTop : rankRest">#{{ i + 1 }}</span>
            <span
              :class="
                trendFor(i) === 'up' ? trendUp : trendFor(i) === 'down' ? trendDown : trendHold
              "
              >{{ trendFor(i) === 'up' ? '▲' : trendFor(i) === 'down' ? '▼' : '—' }}</span
            >
          </div>

          <div class="relative rounded-[4px] overflow-hidden aspect-video bg-surface-sunken">
            <img
              v-if="clip.thumbnailKey"
              :src="clip.thumbnailKey"
              alt=""
              class="w-full h-full object-cover block"
            />
            <DurationBadge :seconds="clip.durationSecs" class="absolute bottom-1 right-1" />
          </div>

          <div class="min-w-0 flex flex-col gap-1">
            <span
              class="font-body text-[13px] font-medium text-text-primary leading-[1.35] line-clamp-2"
              >{{ clip.title }}</span
            >
            <div class="flex items-center gap-1.5 font-mono text-[10px]">
              <GameTag v-if="clip.game" :tag="clip.game.tag" tone="subtle" />
              <AuthorHandle :username="clip.author.username" class="text-neon" />
            </div>
          </div>

          <div
            class="flex flex-col gap-1 text-right font-mono text-[11px] text-text-secondary whitespace-nowrap"
          >
            <span>♥ {{ formatNum(clip.likeCount) }}</span>
            <span class="text-text-muted">{{ formatNum(clip.viewCount) }} plays</span>
          </div>

          <div class="text-text-muted">
            <IconChevronRight :size="16" />
          </div>
        </div>
      </div>

      <!-- RIGHT sidebar -->
      <div class="flex flex-col gap-4 mt-7">
        <div class="bg-surface-raised border border-border rounded-md overflow-hidden">
          <div class="px-4 py-3.5 border-b border-border">
            <span
              class="section-title-bar flex items-center gap-2.5 font-heading font-bold text-sm uppercase text-text-secondary tracking-wider"
              >Hot Games</span
            >
          </div>

          <div
            v-for="g in hotGames"
            :key="g.id"
            class="flex items-center gap-3 px-4 py-2.5 border-b border-border last:border-b-0 cursor-pointer transition-[background] duration-150 hover:bg-surface-overlay"
            @click="router.push({ name: 'games', query: { game: g.slug } })"
          >
            <GameTag :tag="g.tag" variant="square" />
            <div class="min-w-0 flex-1 flex flex-col gap-px">
              <span
                class="font-body text-[13px] font-medium text-text-primary whitespace-nowrap overflow-hidden text-ellipsis"
              >
                {{ g.name }}
              </span>
              <span class="font-mono text-[10px] text-text-muted">
                {{ g.slug }}
              </span>
            </div>
          </div>
        </div>
      </div>
    </div>
  </main>
</template>
