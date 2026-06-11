<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { leaderboards, type LeaderboardWindow } from '@/api/leaderboards'
import { useLatestRequest } from '@/composables/useLatestRequest'
import { formatNum } from '@/lib/format'
import LeaderboardRow from '@/components/LeaderboardRow.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import StatusPanel from '@/components/StatusPanel.vue'

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
  <main class="mx-auto max-w-360 px-8 pt-10 pb-30 max-tablet:px-4 max-tablet:pt-5">
    <!-- Editorial page header -->
    <header class="mb-7">
      <p class="m-0 font-mono text-[10px] uppercase tracking-[0.22em] text-text-secondary">
        <span class="text-ink">Ranked</span> · By likes earned within the window
      </p>
      <h1
        class="m-0 mt-2 font-heading text-[clamp(36px,4.5vw,52px)] font-bold uppercase leading-none text-text-primary"
      >
        The Standings
      </h1>
      <UnderlineTabs
        class="mt-6"
        :tabs="WINDOWS"
        :active="activeWindow"
        @select="selectWindow"
      />
    </header>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load leaderboards.">
      <button
        class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
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
        <SectionHeader roman="I" kicker="Top Clips" />

        <div
          v-if="data.topClips.length === 0"
          class="px-4 py-6 text-center font-mono text-[11px] uppercase tracking-widest text-text-muted"
        >
          No likes recorded in this window.
        </div>

        <LeaderboardRow v-for="entry in data.topClips" :key="entry.clip.id" :entry="entry" />
      </section>

      <!-- RIGHT: top games -->
      <section>
        <SectionHeader roman="II" kicker="Top Games" />

        <div
          v-if="data.topGames.length === 0"
          class="px-4 py-6 text-center font-mono text-[11px] uppercase tracking-widest text-text-muted"
        >
          No game activity yet.
        </div>

        <RouterLink
          v-for="entry in data.topGames"
          :key="entry.game.id"
          :to="{ name: 'game-detail', params: { slug: entry.game.slug } }"
          :aria-label="`#${entry.rank}: ${entry.game.name}`"
          class="group flex items-center gap-3 border-b border-border px-1 py-2.5 outline-none last:border-b-0 focus-visible:bg-surface-raised"
        >
          <span
            class="w-7 shrink-0 text-center font-heading text-[20px] font-bold leading-none transition-colors duration-150 group-hover:text-ink"
            :class="entry.rank <= 3 ? 'text-ink' : 'text-text-muted'"
            >{{ entry.rank }}</span
          >

          <div
            class="aspect-3/4 w-10 shrink-0 overflow-hidden border border-border bg-surface-sunken transition-colors duration-150 group-hover:border-ink"
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
              class="truncate font-heading text-[15px] font-medium uppercase text-text-primary transition-colors duration-150 group-hover:text-ink"
            >
              {{ entry.game.name }}
            </span>
            <span class="font-mono text-[10px] text-text-muted">
              {{ formatNum(entry.windowLikes) }} ♥ · {{ formatNum(entry.clipCount) }} clips
            </span>
          </div>
        </RouterLink>
      </section>
    </div>
  </main>
</template>
