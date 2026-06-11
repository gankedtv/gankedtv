<script setup lang="ts">
// Portrait (3:4) box-art tile linking to /game/:slug. The single shared way to render
// a game in a grid — used by both the catalog (GamesView) and search results so the
// covers stay visually consistent.
//
// Supplementary text under the name (e.g. "12 clips") goes in the `#footer-extra`
// slot — kept out of the component because each call site derives it differently
// (GamesView counts from a loaded feed page; SearchView has no clip count).
//
// Cover rendered as <img> (never as a background-image url()) so a hostile coverUrl
// can't break out of a CSS string. Lazy-loaded for catalogs of hundreds of tiles.

import type { GameListItem } from '@/api/games'
import GameTag from './GameTag.vue'

defineProps<{
  game: GameListItem
}>()
</script>

<template>
  <RouterLink
    :to="{ name: 'game-detail', params: { slug: game.slug } }"
    class="group relative block aspect-3/4 overflow-hidden border border-border bg-surface-raised no-underline transition-colors duration-150 hover:border-ink"
  >
    <!-- alt="" — decorative: the game name is rendered as visible text in this same
         link, so a non-empty alt would make screen readers announce it twice. -->
    <img
      v-if="game.coverUrl"
      :src="game.coverUrl"
      alt=""
      loading="lazy"
      decoding="async"
      class="absolute inset-0 h-full w-full object-cover"
    />
    <!-- Solid legibility band (no gradient scrim — borders and flat fills only). -->
    <div class="absolute inset-x-0 bottom-0 flex flex-col gap-1.5 bg-black/60 p-3">
      <span
        class="font-heading text-lg font-bold uppercase leading-none text-[#f4f1e8] transition-colors duration-150 group-hover:text-ink"
      >
        {{ game.name }}
      </span>
      <div class="flex items-center gap-2">
        <GameTag :tag="game.tag" />
        <slot name="footer-extra" />
      </div>
    </div>
  </RouterLink>
</template>
