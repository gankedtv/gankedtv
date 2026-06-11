<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { search, type SearchResponse } from '@/api/search'
import ClipCard from '@/components/ClipCard.vue'
import GameCoverTile from '@/components/GameCoverTile.vue'
import SectionHeader from '@/components/SectionHeader.vue'
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

// Per-call token: route.query.q can change faster than the fetch resolves (e.g. user
// edits the URL twice in quick succession or types in the navbar while SearchView is
// open). Capturing the seq at the start of load() and re-checking before each state
// write makes sure an out-of-order earlier response can't overwrite the newer one.
let loadSeq = 0

async function load(q: string) {
  const seq = ++loadSeq
  const trimmed = q.trim()
  if (!trimmed) {
    results.value = { clips: [], games: [] }
    lastQuery.value = ''
    // Clear errored too: navigating from a failed `/search?q=foo` back to `/search`
    // shouldn't leave the error panel up over an empty-query state.
    errored.value = false
    loading.value = false
    return
  }
  loading.value = true
  errored.value = false
  try {
    const resp = await search.query(trimmed, { type: 'all', limit: 20 })
    if (seq !== loadSeq) return
    results.value = resp
    lastQuery.value = trimmed
  } catch (err) {
    if (seq !== loadSeq) return
    console.error('search: load failed', err)
    errored.value = true
  } finally {
    if (seq === loadSeq) loading.value = false
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
  <main class="mx-auto max-w-360 px-8 pt-10 pb-30 max-tablet:px-4 max-tablet:pt-5">
    <PageHeader :title="hasQuery() ? `&quot;${lastQuery || String(route.query.q)}&quot;` : 'Search'">
      <template #caption>
        <template v-if="hasQuery()">
          <span class="text-ink">Search</span>&nbsp;· {{ results.clips.length }} clips ·
          {{ results.games.length }} games
        </template>
        <template v-else><span class="text-ink">Search</span>&nbsp;· the archive</template>
      </template>
      <p v-if="!hasQuery()" class="m-0 mt-2 max-w-[56ch] text-[13px] text-text-secondary">
        Type a query in the search bar to look across clips and games.
      </p>
      <hr class="m-0 mt-5 h-px w-full border-0 bg-border" />
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't run the search.">
      <button
        class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
        @click="load(String(route.query.q ?? ''))"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel
      v-else-if="loading && results.clips.length === 0 && results.games.length === 0"
      kind="loading"
      message="Searching"
    />

    <template v-else-if="hasQuery()">
      <!-- Games — same portrait box-art tiles as the catalog (GameCoverTile). -->
      <section class="pt-8">
        <SectionHeader roman="I" kicker="Games" />
        <div
          v-if="results.games.length"
          class="grid grid-cols-5 gap-x-5.5 gap-y-7 pt-6 max-lg:grid-cols-3 max-tablet:grid-cols-2 max-tablet:gap-3"
        >
          <GameCoverTile v-for="g in results.games" :key="g.id" :game="g" />
        </div>
        <StatusPanel v-else kind="empty" message="No games match." />
      </section>

      <!-- Clips -->
      <section class="pt-10">
        <SectionHeader roman="II" kicker="Clips" />
        <div
          v-if="results.clips.length"
          class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-x-5.5 gap-y-7 pt-6"
        >
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
