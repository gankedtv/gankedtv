<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { search, type SearchResponse } from '@/api/search'
import ClipCard from '@/components/ClipCard.vue'
import GameTag from '@/components/GameTag.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'

const route = useRoute()
const router = useRouter()

const results = ref<SearchResponse>({ clips: [], games: [] })
const loading = ref(false)
const errored = ref(false)
// Holds the query the *current* `results` were fetched for. The header reads this
// rather than `route.query.q` so an in-flight fetch doesn't flash a misleading
// "0 results for newWord" while the previous response is still on screen.
const lastQuery = ref('')

async function load(q: string) {
  const trimmed = q.trim()
  if (!trimmed) {
    results.value = { clips: [], games: [] }
    lastQuery.value = ''
    return
  }
  loading.value = true
  errored.value = false
  try {
    results.value = await search.query(trimmed, { type: 'all', limit: 20 })
    lastQuery.value = trimmed
  } catch (err) {
    console.error('search: load failed', err)
    errored.value = true
  } finally {
    loading.value = false
  }
}

watch(
  () => route.query.q,
  (q) => {
    const value = typeof q === 'string' ? q : ''
    void load(value)
  },
  { immediate: true },
)

const hasQuery = () => typeof route.query.q === 'string' && route.query.q.trim().length > 0
</script>

<template>
  <main class="mx-auto max-w-360 px-6 pt-8 pb-30">
    <PageHeader title="Search" pulse>
      <template #caption>
        <template v-if="hasQuery()">
          {{ results.clips.length }} clips · {{ results.games.length }} games for
          <span class="text-text-primary">"{{ lastQuery || route.query.q }}"</span>
        </template>
        <template v-else>Type a query to search clips and games</template>
      </template>
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't run the search.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="load(String(route.query.q ?? ''))"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel
      v-else-if="loading && results.clips.length === 0 && results.games.length === 0"
      kind="loading"
      message="Searching…"
    />

    <template v-else-if="hasQuery()">
      <!-- Games -->
      <section class="mt-10">
        <h2
          class="section-title-bar m-0 mb-5 inline-flex items-center gap-3.5 font-heading text-2xl font-bold uppercase tracking-[0.02em] text-text-primary"
        >
          Games
        </h2>
        <div v-if="results.games.length" class="flex flex-wrap gap-3">
          <RouterLink
            v-for="g in results.games"
            :key="g.id"
            :to="{ name: 'game-detail', params: { slug: g.slug } }"
            class="inline-flex items-center gap-3 rounded-md border border-border bg-surface-raised px-3.5 py-2.5 no-underline transition-colors duration-150 hover:border-brand"
          >
            <GameTag :tag="g.tag" />
            <span class="font-body text-sm text-text-primary">{{ g.name }}</span>
          </RouterLink>
        </div>
        <StatusPanel v-else kind="empty" message="No games match." />
      </section>

      <!-- Clips -->
      <section class="mt-12">
        <h2
          class="section-title-bar m-0 mb-5 inline-flex items-center gap-3.5 font-heading text-2xl font-bold uppercase tracking-[0.02em] text-text-primary"
        >
          Clips
        </h2>
        <div v-if="results.clips.length" class="feed-grid">
          <ClipCard
            v-for="clip in results.clips"
            :key="clip.id"
            :clip="clip"
            @click="router.push({ name: 'clip', params: { id: clip.id } })"
          />
        </div>
        <StatusPanel v-else kind="empty" message="No clips match." />
      </section>
    </template>
  </main>
</template>
