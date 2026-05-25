<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { leaderboards, type LeaderboardWindow } from '@/api/leaderboards'
import { useLatestRequest } from '@/composables/useLatestRequest'
import LeaderboardRow from '@/components/LeaderboardRow.vue'

const props = withDefaults(
  defineProps<{
    slug: string
    window?: LeaderboardWindow
    // Embedded mini-board on GameView. The full /leaderboards page uses the row
    // component directly with its own limit.
    limit?: number
  }>(),
  { window: 'week', limit: 5 },
)

const { data, loading, errored, run } = useLatestRequest(
  () => leaderboards.forGame(props.slug, { window: props.window, limit: props.limit }),
  { label: 'game leaderboard' },
)
const entries = computed(() => data.value?.entries ?? [])

onMounted(run)
// Refetch when the parent navigates between games (slug changes) without unmounting.
watch(() => [props.slug, props.window], run)

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
