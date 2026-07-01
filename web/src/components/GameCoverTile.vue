<script setup lang="ts">
// Portrait (3:4) box-art tile linking to /game/:slug. The single shared way to render
// a game in a grid — used by both the catalog (GamesView) and search results so the
// covers stay visually consistent.
//
// Supplementary text under the name (e.g. "12 clips") goes in the `#footer-extra`
// slot — kept out of the component because each call site derives it differently
// (GamesView counts from a loaded feed page; SearchView has no clip count).
// `rank` renders a catalogue position numeral on the cover (Home / Trending strips).
//
// Cover rendered as <img> (never as a background-image url()) so a hostile coverUrl
// can't break out of a CSS string. Lazy-loaded for catalogs of hundreds of tiles.

import type { GameListItem } from '@/api/games'

defineProps<{
  game: GameListItem
  rank?: number
}>()
</script>

<template>
  <RouterLink
    :to="{ name: 'game-detail', params: { slug: game.slug } }"
    class="group block no-underline"
  >
    <!-- The catalogue tile is the one sanctioned hover transform in the system. -->
    <div
      class="relative aspect-3/4 overflow-hidden rounded-lg border border-border bg-surface-high transition-[border-color,transform] duration-150 group-hover:-translate-y-0.5 group-hover:border-accent-border"
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
      <span
        v-if="rank"
        class="absolute left-2 top-1.5 font-condensed text-[17px] font-black leading-none text-white/60"
        aria-hidden="true"
      >
        {{ String(rank).padStart(2, '0') }}
      </span>
    </div>
    <div class="mt-2 flex flex-col gap-0.5">
      <span
        class="truncate text-[11px] font-bold leading-tight text-text-primary transition-colors duration-150 group-hover:text-accent"
      >
        {{ game.name }}
      </span>
      <slot name="footer-extra" />
    </div>
  </RouterLink>
</template>
