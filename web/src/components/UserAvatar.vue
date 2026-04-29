<script setup lang="ts">
import { computed } from 'vue'

// Accept any object with the bits we need; this matches both AuthorSummary
// from the clips feed and the MeResponse used by AppNav.
export interface AvatarUser {
  username: string
  avatarUrl?: string | null
}

const props = withDefaults(
  defineProps<{
    user: AvatarUser
    size?: number
  }>(),
  { size: 32 },
)

// Stable hash → hue, so the same username always renders the same gradient.
// Tiny djb2 derivative; collisions are fine here, we only want visual variety.
function hashHue(input: string): number {
  let h = 5381
  for (let i = 0; i < input.length; i++) {
    h = ((h << 5) + h + input.charCodeAt(i)) | 0
  }
  return Math.abs(h) % 360
}

const initials = computed(() => {
  const letters = props.user.username.replace(/[^a-zA-Z]/g, '').slice(0, 2).toUpperCase()
  return letters || '??'
})

const fontSize = computed(() => Math.floor(props.size * 0.35))

const bgStyle = computed(() => {
  const hue = hashHue(props.user.username || '')
  const a = `hsl(${hue}, 65%, 45%)`
  const b = `hsl(${(hue + 40) % 360}, 65%, 22%)`
  return { background: `linear-gradient(135deg, ${a}, ${b})` }
})
</script>

<template>
  <span
    class="inline-flex shrink-0 items-center justify-center overflow-hidden rounded-full font-mono font-semibold tracking-tighter text-white"
    :style="{
      width: `${size}px`,
      height: `${size}px`,
      fontSize: `${fontSize}px`,
      ...(user.avatarUrl ? {} : bgStyle),
    }"
  >
    <img
      v-if="user.avatarUrl"
      :src="user.avatarUrl"
      :alt="user.username"
      class="h-full w-full object-cover"
    />
    <template v-else>{{ initials }}</template>
  </span>
</template>
