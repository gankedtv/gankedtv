<script setup lang="ts">
import type { RouteLocationRaw } from 'vue-router'

// Universal Arena section header: mint kicker + plain condensed title on one
// baseline, see-all link pushed right. Every content band opens with this —
// variety comes from the band layout below it, never from the header.
withDefaults(
  defineProps<{
    kicker: string
    title?: string
    moreTo?: RouteLocationRaw
    moreLabel?: string
    /** @deprecated Newsprint prop — ignored; removed once all callers migrate. */
    roman?: string
    /** @deprecated Newsprint prop — ignored; removed once all callers migrate. */
    blurb?: string
  }>(),
  { moreLabel: 'See all →' },
)
</script>

<template>
  <div class="mb-4 flex items-baseline gap-3">
    <span class="text-[10px] font-bold uppercase leading-none tracking-[0.14em] text-accent">
      {{ kicker }}
    </span>
    <h2
      v-if="title"
      class="m-0 font-condensed text-xl font-extrabold uppercase leading-none tracking-wide text-text-primary"
    >
      {{ title }}
    </h2>
    <slot name="right">
      <RouterLink
        v-if="moreTo"
        :to="moreTo"
        class="ml-auto shrink-0 text-[11px] font-semibold text-accent transition-colors duration-150 hover:underline"
      >
        {{ moreLabel }}
      </RouterLink>
    </slot>
  </div>
</template>
