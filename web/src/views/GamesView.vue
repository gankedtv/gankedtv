<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { clips, type ClipFeedItem } from '@/api/clips'
import ClipCard from '@/components/ClipCard.vue'
import GameCoverTile from '@/components/GameCoverTile.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'

const router = useRouter()

const allGames = ref<GameListItem[]>([])
const allClips = ref<ClipFeedItem[]>([])
const loading = ref(false)
const errored = ref(false)

// Per-game clip counts are derived from the loaded feed page — fine as a rough
// indicator on the catalog tiles. The authoritative count for a game lives on
// the game-detail page (clipCount on GameDetail).
const clipCountByGame = computed(() => {
  const counts = new Map<string, number>()
  for (const c of allClips.value) {
    if (c.game) counts.set(c.game.slug, (counts.get(c.game.slug) ?? 0) + 1)
  }
  return counts
})

const featuredClips = computed(() => allClips.value.slice(0, 12))

async function load() {
  loading.value = true
  errored.value = false
  try {
    const [gs, feed] = await Promise.all([
      gamesApi.list(50, { hasClips: true }),
      clips.feed({ limit: 100 }),
    ])
    allGames.value = gs
    allClips.value = feed.items
  } catch {
    errored.value = true
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <main class="mx-auto max-w-300 px-7 pt-7 pb-16 max-tablet:px-4">
    <PageHeader title="Games">
      <template #caption>
        {{ allGames.length }} games · {{ allClips.length }} clips loaded
      </template>
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load games.">
      <button
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="load"
      >
        Retry
      </button>
    </StatusPanel>

    <template v-else>
      <!-- Box-art grid — portrait (3:4) game covers, each links to /game/:slug.
           Tile rendering lives in GameCoverTile so SearchView's games section
           stays visually identical. -->
      <div class="mt-7 grid grid-cols-5 gap-3 max-lg:grid-cols-3 max-tablet:grid-cols-2">
        <GameCoverTile v-for="g in allGames" :key="g.id" :game="g">
          <template #footer-extra>
            <!-- Per-game clip counts are derived from the loaded feed page (rough
                 indicator); the authoritative count is on the game-detail page. -->
            <span class="text-[10px] text-text-muted">
              {{ clipCountByGame.get(g.slug) ?? 0 }} clips
            </span>
          </template>
        </GameCoverTile>
      </div>

      <!-- Latest clips across all games -->
      <section class="mt-8 border-t border-border pt-7">
        <SectionHeader kicker="New" title="Latest Clips" />

        <StatusPanel
          v-if="loading && featuredClips.length === 0"
          kind="loading"
          message="Loading"
        />
        <div
          v-else-if="featuredClips.length"
          class="grid grid-cols-4 gap-3.5 max-lg:grid-cols-2 max-tablet:grid-cols-1"
        >
          <ClipCard
            v-for="clip in featuredClips"
            :key="clip.id"
            :clip="clip"
            @click="router.push({ name: 'clip', params: { id: clip.id } })"
          />
        </div>
        <p v-else class="m-0 text-[11px] text-text-muted">No clips yet.</p>
      </section>
    </template>
  </main>
</template>
