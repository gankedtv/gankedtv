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
  <main class="mx-auto max-w-360 px-8 pt-10 pb-30 max-tablet:px-4 max-tablet:pt-5">
    <PageHeader title="The Catalogue">
      <template #caption>
        <span class="text-ink">Vol 1</span>&nbsp;· {{ allGames.length }} games ·
        {{ allClips.length }} clips loaded
      </template>
      <p class="m-0 mt-2 max-w-[56ch] text-[13px] leading-normal text-text-secondary">
        Every clip is filed under its game. Pick one to see all its clips.
      </p>
      <hr class="m-0 mt-5 h-px w-full border-0 bg-border" />
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load games.">
      <button
        class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
        @click="load"
      >
        Retry
      </button>
    </StatusPanel>

    <template v-else>
      <!-- Box-art wall — portrait (3:4) game covers, each links to /game/:slug.
           Tile rendering lives in GameCoverTile so SearchView's games section
           stays visually identical. -->
      <div
        class="mt-7 grid grid-cols-5 gap-x-5.5 gap-y-7 max-lg:grid-cols-3 max-tablet:grid-cols-2 max-tablet:gap-3"
      >
        <GameCoverTile v-for="g in allGames" :key="g.id" :game="g">
          <template #footer-extra>
            <!-- Per-game clip counts are derived from the loaded feed page (rough
                 indicator); the authoritative count is on the game-detail page. -->
            <span class="font-mono text-[10px] tracking-[0.08em] text-[#f4f1e8]/80">
              {{ clipCountByGame.get(g.slug) ?? 0 }} clips
            </span>
          </template>
        </GameCoverTile>
      </div>

      <!-- Featured clips teaser -->
      <section class="pt-12">
        <SectionHeader roman="II" kicker="Featured" title="Across All Games" />

        <StatusPanel
          v-if="loading && featuredClips.length === 0"
          kind="loading"
          message="Loading"
        />
        <div
          v-else-if="featuredClips.length"
          class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-x-5.5 gap-y-7 pt-6"
        >
          <ClipCard
            v-for="clip in featuredClips"
            :key="clip.id"
            :clip="clip"
            @click="router.push({ name: 'clip', params: { id: clip.id } })"
          />
        </div>
        <div
          v-else
          class="border-y border-border p-8 text-center font-mono text-sm uppercase tracking-widest text-text-muted"
        >
          No clips yet.
        </div>
      </section>
    </template>
  </main>
</template>
