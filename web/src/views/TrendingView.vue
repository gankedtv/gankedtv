<script setup lang="ts">
import { ref, onMounted, watch } from 'vue'
import { clips, type ClipFeedItem } from '@/api/clips'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { formatNum } from '@/lib/format'
import GameTag from '@/components/GameTag.vue'
import DurationBadge from '@/components/DurationBadge.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'
import IconChevronRight from '@/components/icons/IconChevronRight.vue'

// Only 24h and 7d hit the server-side trending feed today. The other windows surface the
// UX intent (and the issue's "drop fake arrows for v1" sentiment is to ship what the
// server actually supports), so they're rendered but aria-disabled until follow-up windows land.
type TrendingWindow = '24h' | '7d'
const TIME_WINDOWS = [
  { key: '1h', label: 'Last hour', enabled: false },
  { key: '24h', label: '24 hours', enabled: true },
  { key: '7d', label: 'This week', enabled: true },
  { key: '30d', label: 'This month', enabled: false },
  { key: 'all', label: 'All time', enabled: false },
] as const

const timeWindow = ref<TrendingWindow>('24h')

const topClips = ref<ClipFeedItem[]>([])
const loading = ref(false)
const errored = ref(false)

const hotGames = ref<GameListItem[]>([])

async function load() {
  loading.value = true
  errored.value = false
  try {
    const [feed, games] = await Promise.all([
      clips.feed({ sort: 'trending', window: timeWindow.value, limit: 50 }),
      gamesApi.list(8),
    ])
    topClips.value = feed.items.slice(0, 10)
    hotGames.value = games
  } catch (err) {
    console.error('trending: load failed', err)
    errored.value = true
  } finally {
    loading.value = false
  }
}

onMounted(load)

// Window change → re-fetch. Hot games don't depend on the window, so reloading them is
// wasted work but the simplicity wins; the request is cheap and small.
watch(timeWindow, load)

function selectWindow(key: string, enabled: boolean) {
  if (!enabled || key === timeWindow.value) return
  timeWindow.value = key as TrendingWindow
}

const timeBtnBase =
  'px-3.5 py-1.5 rounded-sm font-mono text-[11px] uppercase tracking-[0.06em] border-none'
const timeBtnActive = `${timeBtnBase} bg-brand text-white cursor-pointer transition-[background] duration-150`
const timeBtnInactiveEnabled = `${timeBtnBase} bg-transparent text-text-secondary cursor-pointer transition-[color] duration-150`
const timeBtnInactiveDisabled = `${timeBtnBase} bg-transparent text-text-secondary opacity-50 cursor-not-allowed`

const rankBase = 'font-heading font-bold text-[28px] leading-none'
const rankTop = `${rankBase} text-brand-light`
const rankRest = `${rankBase} text-text-muted`
</script>

<template>
  <main class="mx-auto max-w-360 px-6 pt-8 pb-30">
    <PageHeader title="Trending">
      <template #caption>Ranked by recent engagement (likes × 3 + views, decayed by age)</template>

      <p id="time-window-hint" class="sr-only">
        24-hour and 7-day windows are available; the other ranges are coming soon.
      </p>
      <div
        class="mt-5 inline-flex gap-0.5 p-1 bg-surface-raised border border-border rounded-sm"
        role="group"
        aria-label="Trending time window"
      >
        <button
          v-for="tw in TIME_WINDOWS"
          :key="tw.key"
          type="button"
          :class="
            timeWindow === tw.key
              ? timeBtnActive
              : tw.enabled
                ? timeBtnInactiveEnabled
                : timeBtnInactiveDisabled
          "
          :aria-disabled="!tw.enabled"
          :aria-pressed="tw.key === timeWindow"
          aria-describedby="time-window-hint"
          @click="selectWindow(tw.key, tw.enabled)"
        >
          {{ tw.label }}
        </button>
      </div>
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load trending.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="load"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel v-else-if="loading && topClips.length === 0" kind="loading" message="Loading…" />

    <StatusPanel
      v-else-if="!loading && topClips.length === 0"
      kind="empty"
      message="No clips trending yet — check back soon."
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
            >Top 10 right now</span
          >
        </div>

        <RouterLink
          v-for="(clip, i) in topClips"
          :key="clip.id"
          :to="{ name: 'clip', params: { id: clip.id } }"
          :aria-label="`#${i + 1}: ${clip.title}`"
          class="grid grid-cols-[40px_120px_1fr_auto_auto] gap-4 items-center px-4 py-3 transition-[background] duration-150 border-b border-border last:border-b-0 outline-none hover:bg-surface-overlay focus-visible:bg-surface-overlay focus-visible:ring-2 focus-visible:ring-brand-light"
        >
          <span :class="i < 3 ? rankTop : rankRest">#{{ i + 1 }}</span>

          <div class="relative rounded-[4px] overflow-hidden aspect-video bg-surface-sunken">
            <img :src="clip.thumbnailUrl" alt="" class="w-full h-full object-cover block" />
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
        </RouterLink>
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

          <RouterLink
            v-for="g in hotGames"
            :key="g.id"
            :to="{ name: 'games', query: { game: g.slug } }"
            :aria-label="`Filter feed by ${g.name}`"
            class="flex items-center gap-3 px-4 py-2.5 border-b border-border last:border-b-0 transition-[background] duration-150 outline-none hover:bg-surface-overlay focus-visible:bg-surface-overlay focus-visible:ring-2 focus-visible:ring-brand-light"
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
          </RouterLink>
        </div>
      </div>
    </div>
  </main>
</template>
