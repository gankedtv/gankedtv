<script setup lang="ts">
// Consistent `@username` rendering. Pass `as="link"` to make it a router-link
// to /user/<username>; otherwise it renders as plain text. Color, size, and
// other utility classes are intentionally not props — apply Tailwind on the
// component and they fall through to the rendered root, so callers stay in
// control of the visual context (mint in cards, primary in hero copy, etc.).

defineProps<{
  username: string
  as?: 'link' | 'span'
}>()
</script>

<template>
  <span>
    <RouterLink
      v-if="as === 'link'"
      :to="{ name: 'user', params: { username } }"
      class="transition-opacity hover:opacity-80"
      >@{{ username }}</RouterLink
    >
    <template v-else>@{{ username }}</template>
  </span>
</template>
