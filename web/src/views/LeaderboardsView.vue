<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { leaderboards, type LeaderboardWindow } from '@/api/leaderboards'
import { useLatestRequest } from '@/composables/useLatestRequest'
import { formatNum } from '@/lib/format'
import LeaderboardRow from '@/components/LeaderboardRow.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'

// Server vocabulary is week|month|all — see LeaderboardWindow.cs. Tabs render in the
// order the user reads time: shortest window first, all-time last.
const WINDOWS: { key: LeaderboardWindow; label: string }[] = [
  { key: 'week', label: 'This Week' },
  { key: 'month', label: 'This Month' },
  { key: 'all', label: 'All Time' },
]

const activeWindow = ref<LeaderboardWindow>('week')
const { data, loading, errored, run } = useLatestRequest(
  () => leaderboards.global({ window: activeWindow.value, clipsLimit: 10, gamesLimit: 10 }),
  { label: 'leaderboards' },
)

onMounted(run)
watch(activeWindow, run)

function selectWindow(key: LeaderboardWindow) {
  if (key === activeWindow.value) return
  activeWindow.value = key
}
</script>

<template>
  <main class="mx-auto max-w-300 px-7 pt-7 pb-16 max-tablet:px-4">
    <PageHeader title="Leaderboards" class="mb-7">
      <template #caption>By likes earned within the window</template>
      <UnderlineTabs class="mt-5" :tabs="WINDOWS" :active="activeWindow" @select="selectWindow" />
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load leaderboards.">
      <button
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="run"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel v-else-if="loading && !data" kind="loading" message="Loading" />

    <StatusPanel
      v-else-if="data && data.topClips.length === 0 && data.topGames.length === 0"
      kind="empty"
      message="No clips have been liked in this window yet."
    />

    <div
      v-else-if="data"
      class="grid grid-cols-[minmax(0,1fr)_340px] items-start gap-9 max-[960px]:grid-cols-1"
    >
      <!-- LEFT: top clips -->
      <section>
        <SectionHeader kicker="Ranked" title="Top Clips" />

        <div
          v-if="data.topClips.length === 0"
          class="px-4 py-6 text-center text-xs text-text-muted"
        >
          No likes recorded in this window.
        </div>

        <LeaderboardRow v-for="entry in data.topClips" :key="entry.clip.id" :entry="entry" />
      </section>

      <!-- RIGHT: top games -->
      <section>
        <SectionHeader kicker="Ranked" title="Top Games" />

        <div
          v-if="data.topGames.length === 0"
          class="px-4 py-6 text-center text-xs text-text-muted"
        >
          No game activity yet.
        </div>

        <RouterLink
          v-for="entry in data.topGames"
          :key="entry.game.id"
          :to="{ name: 'game-detail', params: { slug: entry.game.slug } }"
          :aria-label="`#${entry.rank}: ${entry.game.name}`"
          class="group flex items-center gap-3 border-b border-border px-1 py-2.5 outline-none last:border-b-0 focus-visible:bg-surface-high"
        >
          <span
            class="w-7 shrink-0 text-center font-condensed text-[22px] font-black leading-none"
            :class="entry.rank === 1 ? 'text-accent' : 'text-text-muted'"
            >{{ entry.rank }}</span
          >

          <div
            class="aspect-3/4 w-10 shrink-0 overflow-hidden rounded-md border border-border bg-surface-high transition-colors duration-150 group-hover:border-border-strong"
          >
            <img
              v-if="entry.coverUrl"
              :src="entry.coverUrl"
              alt=""
              class="block h-full w-full object-cover"
            />
          </div>

          <div class="flex min-w-0 flex-1 flex-col gap-px">
            <span
              class="truncate text-[11.5px] font-semibold text-text-primary transition-colors duration-150 group-hover:text-accent"
            >
              {{ entry.game.name }}
            </span>
            <span class="text-[10px] text-text-muted">
              {{ formatNum(entry.windowLikes) }} ♥ · {{ formatNum(entry.clipCount) }} clips
            </span>
          </div>
        </RouterLink>
      </section>
    </div>
  </main>
</template>
