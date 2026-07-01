<script setup lang="ts" generic="K extends string">
import type { RouteLocationRaw } from 'vue-router'

// Underline-style tab control used across the app (HomeView feed tabs,
// UserView Clips/Liked, UserFollowListView Followers/Following). A tab is either
// a RouterLink (when `to` is set) or a <button> that emits `select`. The two
// modes coexist so callers can express "navigate to a new route" vs "swap local
// state" without each caller re-implementing the styling.
//
// `disabled` tabs render visibly (not hidden) so users can see what's coming —
// they never emit or navigate.
defineProps<{
  tabs: Array<{ key: K; label: string; to?: RouteLocationRaw; disabled?: boolean }>
  active: K
}>()

defineEmits<{
  select: [key: K]
}>()
</script>

<template>
  <div class="flex items-center border-b border-border">
    <template v-for="t in tabs" :key="t.key">
      <span
        v-if="t.disabled"
        class="relative -mb-px cursor-not-allowed whitespace-nowrap border-b-2 border-transparent px-4 py-2.5 text-xs font-semibold text-text-muted opacity-40"
        aria-disabled="true"
      >
        {{ t.label }}
      </span>
      <RouterLink
        v-else-if="t.to"
        :to="t.to"
        :class="[
          'relative -mb-px cursor-pointer whitespace-nowrap border-b-2 bg-transparent px-4 py-2.5 text-xs font-semibold no-underline transition-colors duration-150',
          t.key === active
            ? 'border-accent text-text-primary'
            : 'border-transparent text-text-muted hover:text-text-secondary',
        ]"
      >
        {{ t.label }}
      </RouterLink>
      <button
        v-else
        :class="[
          'relative -mb-px cursor-pointer whitespace-nowrap border-b-2 bg-transparent px-4 py-2.5 text-xs font-semibold transition-colors duration-150',
          t.key === active
            ? 'border-accent text-text-primary'
            : 'border-transparent text-text-muted hover:text-text-secondary',
        ]"
        @click="$emit('select', t.key)"
      >
        {{ t.label }}
      </button>
    </template>
  </div>
</template>
