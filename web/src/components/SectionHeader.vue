<script setup lang="ts">
import type { RouteLocationRaw } from 'vue-router'

// Universal Newsprint section header: kicker row (roman numeral in ink +
// label, optional more→ link), oversized condensed title, optional blurb,
// hairline rule. Every content band on every page opens with this pattern —
// variety comes from the band layout below it, never from the header.
withDefaults(
  defineProps<{
    kicker: string
    roman?: string
    title?: string
    blurb?: string
    moreTo?: RouteLocationRaw
    moreLabel?: string
  }>(),
  { moreLabel: 'more →' },
)
</script>

<template>
  <div class="mb-3.5 flex items-end justify-between gap-6">
    <div class="min-w-0">
      <p
        class="font-mono text-[10px] font-medium uppercase leading-none tracking-[0.22em] text-text-secondary"
      >
        <span v-if="roman" class="mr-2 text-ink">{{ roman }}</span
        >{{ kicker }}
      </p>
      <h2
        v-if="title"
        class="mt-2 font-heading text-[clamp(28px,3vw,38px)] font-bold uppercase leading-[1.05] text-text-primary"
      >
        {{ title }}
      </h2>
      <p v-if="blurb" class="mt-2 max-w-[56ch] text-[13px] text-text-secondary">
        {{ blurb }}
      </p>
    </div>
    <slot name="right">
      <RouterLink
        v-if="moreTo"
        :to="moreTo"
        class="shrink-0 font-mono text-[11px] uppercase tracking-[0.12em] text-text-secondary transition-colors duration-150 hover:text-ink"
      >
        {{ moreLabel }}
      </RouterLink>
    </slot>
  </div>
  <hr class="m-0 h-px w-full border-0 bg-border" />
</template>
