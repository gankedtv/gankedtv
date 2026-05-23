<script setup lang="ts" generic="K extends string">
import type { RouteLocationRaw } from 'vue-router'

// Underline-style tab control used across the app (HomeView Latest/Following,
// UserView Clips/Liked, UserFollowListView Followers/Following). A tab is either
// a RouterLink (when `to` is set) or a <button> that emits `select`. The two
// modes coexist so callers can express "navigate to a new route" vs "swap local
// state" without each caller re-implementing the styling.
defineProps<{
  tabs: Array<{ key: K; label: string; to?: RouteLocationRaw }>
  active: K
}>()

defineEmits<{
  select: [key: K]
}>()
</script>

<template>
  <div class="flex items-center border-b border-border">
    <template v-for="t in tabs" :key="t.key">
      <RouterLink
        v-if="t.to"
        :to="t.to"
        :class="[
          'relative cursor-pointer border-none bg-transparent px-4.5 py-3 font-mono text-xs uppercase tracking-[0.08em] no-underline transition-colors duration-150 hover:text-text-primary',
          t.key === active
            ? `text-text-primary after:absolute after:right-0 after:-bottom-px after:left-0 after:h-0.5 after:rounded-t-xs after:bg-brand-light after:content-['']`
            : 'text-text-muted',
        ]"
      >
        {{ t.label }}
      </RouterLink>
      <button
        v-else
        :class="[
          'relative cursor-pointer border-none bg-transparent px-4.5 py-3 font-mono text-xs uppercase tracking-[0.08em] transition-colors duration-150 hover:text-text-primary',
          t.key === active
            ? `text-text-primary after:absolute after:right-0 after:-bottom-px after:left-0 after:h-0.5 after:rounded-t-xs after:bg-brand-light after:content-['']`
            : 'text-text-muted',
        ]"
        @click="$emit('select', t.key)"
      >
        {{ t.label }}
      </button>
    </template>
  </div>
</template>
