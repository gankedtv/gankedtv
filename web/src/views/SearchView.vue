<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { search, type SearchResponse } from '@/api/search'
import ClipCard from '@/components/ClipCard.vue'
import GameCoverTile from '@/components/GameCoverTile.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'
import UserAvatar from '@/components/UserAvatar.vue'

const route = useRoute()
const router = useRouter()

const emptyResults = (): SearchResponse => ({ clips: [], games: [], users: [] })
const results = ref<SearchResponse>(emptyResults())
const hasResults = () =>
  results.value.clips.length > 0 || results.value.games.length > 0 || results.value.users.length > 0
const loading = ref(false)
const errored = ref(false)
// Holds the query the *current* `results` were fetched for. The header reads this
// rather than `route.query.q` so an in-flight fetch doesn't flash a misleading
// "0 results for newWord" while the previous response is still on screen.
const lastQuery = ref('')

// Page-level search input. Submitting pushes the query into the URL — the
// `?q=` watcher below owns the actual fetch, same as searches from the nav bar.
const queryInput = ref('')

function submitSearch() {
  const q = queryInput.value.trim()
  router.push({ name: 'search', query: q ? { q } : {} })
}

// Per-call token: route.query.q can change faster than the fetch resolves (e.g. user
// edits the URL twice in quick succession or types in the navbar while SearchView is
// open). Capturing the seq at the start of load() and re-checking before each state
// write makes sure an out-of-order earlier response can't overwrite the newer one.
let loadSeq = 0

async function load(q: string) {
  const seq = ++loadSeq
  const trimmed = q.trim()
  if (!trimmed) {
    results.value = emptyResults()
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
    queryInput.value = value
    void load(value)
  },
  { immediate: true },
)

const hasQuery = () => typeof route.query.q === 'string' && route.query.q.trim().length > 0
</script>

<template>
  <main class="mx-auto max-w-300 px-7 pt-7 pb-16 max-tablet:px-4">
    <PageHeader :title="hasQuery() ? `“${lastQuery || String(route.query.q)}”` : 'Search'">
      <template #caption>
        <template v-if="hasQuery()">
          {{ results.clips.length }} clips · {{ results.games.length }} games ·
          {{ results.users.length }} players
        </template>
        <template v-else>Clips, games, and players</template>
      </template>
      <form class="mt-5" role="search" @submit.prevent="submitSearch">
        <input
          v-model="queryInput"
          type="search"
          placeholder="Search for clips, games, or players"
          aria-label="Search"
          class="h-12 w-full rounded-lg border border-border-strong bg-surface-high px-4 text-sm text-text-primary placeholder:text-text-muted focus:border-accent focus:outline-none"
        />
      </form>
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't run the search.">
      <button
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="load(String(route.query.q ?? ''))"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel v-else-if="loading && !hasResults()" kind="loading" message="Searching" />

    <template v-else-if="hasQuery()">
      <!-- No results across any section — one combined empty state. -->
      <StatusPanel
        v-if="!hasResults()"
        kind="empty"
        :message="`No results for “${lastQuery}”. Try a different term.`"
      />

      <!-- Clips — section headers render only when the section has results. -->
      <section v-if="results.clips.length" class="mt-8">
        <SectionHeader kicker="Results" title="Clips" />
        <div class="grid grid-cols-4 gap-3.5 max-lg:grid-cols-2 max-tablet:grid-cols-1">
          <ClipCard
            v-for="clip in results.clips"
            :key="clip.id"
            :clip="clip"
            @click="router.push({ name: 'clip', params: { id: clip.id } })"
          />
        </div>
      </section>

      <!-- Games — same portrait box-art tiles as the catalog (GameCoverTile). -->
      <section
        v-if="results.games.length"
        class="mt-8"
        :class="results.clips.length ? 'border-t border-border pt-7' : ''"
      >
        <SectionHeader kicker="Results" title="Games" />
        <div class="grid grid-cols-5 gap-3 max-lg:grid-cols-3 max-tablet:grid-cols-2">
          <GameCoverTile v-for="g in results.games" :key="g.id" :game="g" />
        </div>
      </section>

      <!-- Players — same avatar + handle rows as the follow lists. -->
      <section
        v-if="results.users.length"
        class="mt-8"
        :class="results.clips.length || results.games.length ? 'border-t border-border pt-7' : ''"
      >
        <SectionHeader kicker="Results" title="Players" />
        <ul class="m-0 flex list-none flex-col gap-1 p-0">
          <li v-for="u in results.users" :key="u.id">
            <RouterLink
              :to="{ name: 'user', params: { username: u.username } }"
              class="flex items-center gap-3 rounded-lg px-2 py-2 text-sm font-semibold text-text-primary no-underline transition-colors duration-150 hover:bg-surface-high"
            >
              <UserAvatar :user="u" :size="32" />
              <span class="truncate">{{ u.username }}</span>
            </RouterLink>
          </li>
        </ul>
      </section>
    </template>
  </main>
</template>
