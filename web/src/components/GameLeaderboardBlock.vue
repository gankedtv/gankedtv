<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { leaderboards, type LeaderboardWindow } from '@/api/leaderboards'
import { useLatestRequest } from '@/composables/useLatestRequest'
import LeaderboardRow from '@/components/LeaderboardRow.vue'
import SectionHeader from '@/components/SectionHeader.vue'

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
watch(() => [props.slug, props.window, props.limit], run)

const windowLabel = {
  week: 'This Week',
  month: 'This Month',
  all: 'All Time',
} as const
</script>

<template>
  <!-- Band, not card: section header + border-separated rows directly on the
       page surface. The parent owns the surrounding section spacing. -->
  <section v-if="loading || errored || entries.length > 0" class="mb-10">
    <SectionHeader
      :kicker="windowLabel[window]"
      title="Top Clips"
      :more-to="{ name: 'leaderboards' }"
      more-label="All leaderboards →"
    />

    <div
      v-if="loading && entries.length === 0"
      class="px-4 py-6 text-center text-xs text-text-muted"
    >
      Loading
    </div>

    <div
      v-else-if="errored && entries.length === 0"
      class="px-4 py-6 text-center text-xs text-text-muted"
    >
      Couldn't load leaderboard.
    </div>

    <LeaderboardRow v-for="entry in entries" :key="entry.clip.id" :entry="entry" />
  </section>
</template>
