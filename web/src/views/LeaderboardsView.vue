<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import {
  leaderboards,
  type GlobalLeaderboardResponse,
  type LeaderboardWindow,
} from '@/api/leaderboards'
import { formatNum } from '@/lib/format'
import LeaderboardRow from '@/components/LeaderboardRow.vue'
import PageHeader from '@/components/PageHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'

// Server vocabulary is week|month|all — see LeaderboardWindow.cs. Tabs render in the
// order the user reads time: shortest window first, all-time last.
const WINDOWS: { key: LeaderboardWindow; label: string }[] = [
  { key: 'week', label: 'This Week' },
  { key: 'month', label: 'This Month' },
  { key: 'all', label: 'All Time' },
]

const activeWindow = ref<LeaderboardWindow>('week')
const data = ref<GlobalLeaderboardResponse | null>(null)
const loading = ref(false)
const errored = ref(false)

// Monotonic load token: rapid tab toggles (week → month → week) shouldn't let a slow
// earlier response overwrite a newer one. Same pattern as TrendingView.
let latestLoadId = 0

async function load() {
  const id = ++latestLoadId
  loading.value = true
  errored.value = false
  try {
    const resp = await leaderboards.global({
      window: activeWindow.value,
      clipsLimit: 10,
      gamesLimit: 10,
    })
    if (id !== latestLoadId) return
    data.value = resp
  } catch (err) {
    if (id !== latestLoadId) return
    console.error('leaderboards: load failed', err)
    errored.value = true
  } finally {
    if (id === latestLoadId) loading.value = false
  }
}

onMounted(load)
watch(activeWindow, load)

function selectWindow(key: LeaderboardWindow) {
  if (key === activeWindow.value) return
  activeWindow.value = key
}

const timeBtnBase =
  'px-3.5 py-1.5 rounded-sm font-mono text-[11px] uppercase tracking-[0.06em] border-none'
const timeBtnActive = `${timeBtnBase} bg-brand text-white cursor-pointer transition-[background] duration-150`
const timeBtnInactive = `${timeBtnBase} bg-transparent text-text-secondary cursor-pointer transition-[color] duration-150`
</script>

<template>
  <main class="mx-auto max-w-360 px-6 pt-8 pb-30">
    <PageHeader title="Leaderboards">
      <template #caption
        >Most-liked clips and games, ranked by likes earned within the window.</template
      >

      <div
        class="mt-5 inline-flex gap-0.5 p-1 bg-surface-raised border border-border rounded-sm"
        role="group"
        aria-label="Leaderboard time window"
      >
        <button
          v-for="tw in WINDOWS"
          :key="tw.key"
          type="button"
          :class="activeWindow === tw.key ? timeBtnActive : timeBtnInactive"
          :aria-pressed="activeWindow === tw.key"
          @click="selectWindow(tw.key)"
        >
          {{ tw.label }}
        </button>
      </div>
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load leaderboards.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="load"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel v-else-if="loading && !data" kind="loading" message="Loading…" />

    <StatusPanel
      v-else-if="data && data.topClips.length === 0 && data.topGames.length === 0"
      kind="empty"
      message="No clips have been liked in this window yet."
    />

    <div
      v-else-if="data"
      class="grid grid-cols-[minmax(0,1fr)_340px] gap-7 items-start max-[960px]:grid-cols-1 mt-7"
    >
      <!-- LEFT: top clips -->
      <div class="bg-surface-raised border border-border rounded-md overflow-hidden">
        <div class="px-4 py-3.5 border-b border-border">
          <span class="font-heading font-bold text-sm uppercase text-text-secondary tracking-wider">
            Top Clips
          </span>
        </div>

        <div
          v-if="data.topClips.length === 0"
          class="px-4 py-6 font-mono text-[11px] uppercase tracking-widest text-text-muted text-center"
        >
          No likes recorded in this window.
        </div>

        <LeaderboardRow v-for="entry in data.topClips" :key="entry.clip.id" :entry="entry" />
      </div>

      <!-- RIGHT: top games -->
      <div class="flex flex-col gap-4">
        <div class="bg-surface-raised border border-border rounded-md overflow-hidden">
          <div class="px-4 py-3.5 border-b border-border">
            <span
              class="font-heading font-bold text-sm uppercase text-text-secondary tracking-wider"
            >
              Top Games
            </span>
          </div>

          <div
            v-if="data.topGames.length === 0"
            class="px-4 py-6 font-mono text-[11px] uppercase tracking-widest text-text-muted text-center"
          >
            No game activity yet.
          </div>

          <RouterLink
            v-for="entry in data.topGames"
            :key="entry.game.id"
            :to="{ name: 'game-detail', params: { slug: entry.game.slug } }"
            :aria-label="`#${entry.rank}: ${entry.game.name}`"
            class="flex items-center gap-3 px-4 py-2.5 border-b border-border last:border-b-0 transition-[background] duration-150 outline-none hover:bg-surface-overlay focus-visible:bg-surface-overlay focus-visible:ring-2 focus-visible:ring-brand-light"
          >
            <span
              class="font-heading font-bold text-[20px] leading-none w-7 shrink-0 text-center"
              :class="entry.rank <= 3 ? 'text-brand-light' : 'text-text-muted'"
              >#{{ entry.rank }}</span
            >

            <div class="min-w-0 flex-1 flex flex-col gap-px">
              <span class="font-body text-[13px] font-medium text-text-primary truncate">
                {{ entry.game.name }}
              </span>
              <span class="font-mono text-[10px] text-text-muted">
                {{ formatNum(entry.windowLikes) }} ♥ · {{ formatNum(entry.clipCount) }} clips
              </span>
            </div>
          </RouterLink>
        </div>
      </div>
    </div>
  </main>
</template>
