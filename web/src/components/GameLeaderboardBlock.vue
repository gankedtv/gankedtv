<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { leaderboards, type LeaderboardEntry, type LeaderboardWindow } from '@/api/leaderboards'
import LeaderboardRow from '@/components/LeaderboardRow.vue'

const props = withDefaults(
  defineProps<{
    slug: string
    window?: LeaderboardWindow
    // Mini view shows the top-3 only — fits a sidebar/embed nicely. The full /leaderboards
    // page uses the row component directly with its own limit.
    limit?: number
  }>(),
  { window: 'week', limit: 5 },
)

const entries = ref<LeaderboardEntry[]>([])
const loading = ref(false)
const errored = ref(false)
let latestLoadId = 0

async function load() {
  const id = ++latestLoadId
  loading.value = true
  errored.value = false
  try {
    const resp = await leaderboards.forGame(props.slug, {
      window: props.window,
      limit: props.limit,
    })
    if (id !== latestLoadId) return
    entries.value = resp.entries
  } catch (err) {
    if (id !== latestLoadId) return
    console.error('game leaderboard: load failed', err)
    errored.value = true
  } finally {
    if (id === latestLoadId) loading.value = false
  }
}

onMounted(load)
// Refetch when the parent navigates between games (slug changes) without unmounting.
watch(() => [props.slug, props.window], load)

const windowLabel = {
  week: 'This Week',
  month: 'This Month',
  all: 'All Time',
} as const
</script>

<template>
  <section
    v-if="loading || errored || entries.length > 0"
    class="mb-8 overflow-hidden rounded-md border border-border bg-surface-raised"
  >
    <div class="flex items-center justify-between border-b border-border px-4 py-3.5">
      <span class="font-heading text-sm font-bold tracking-wider text-text-secondary uppercase">
        Top {{ windowLabel[window] }}
      </span>
      <RouterLink
        to="/leaderboards"
        class="font-mono text-[10px] tracking-widest text-text-muted uppercase no-underline hover:text-text-primary"
      >
        All leaderboards →
      </RouterLink>
    </div>

    <div
      v-if="loading && entries.length === 0"
      class="px-4 py-6 text-center font-mono text-[11px] tracking-widest text-text-muted uppercase"
    >
      Loading…
    </div>

    <div
      v-else-if="errored && entries.length === 0"
      class="px-4 py-6 text-center font-mono text-[11px] tracking-widest text-text-muted uppercase"
    >
      Couldn't load leaderboard.
    </div>

    <LeaderboardRow v-for="entry in entries" :key="entry.clip.id" :entry="entry" />
  </section>
</template>
