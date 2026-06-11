<script setup lang="ts">
import { computed, ref, watch } from 'vue'

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
  const letters = props.user.username
    .replace(/[^a-zA-Z]/g, '')
    .slice(0, 2)
    .toUpperCase()
  return letters || '??'
})

const fontSize = computed(() => Math.floor(props.size * 0.35))

// Flat single-hue fill — low-chroma so initials stay legible and the palette
// stays print-adjacent (gradients are banned outside thumbnail fallbacks).
const bgStyle = computed(() => {
  const hue = hashHue(props.user.username)
  return { background: `hsl(${hue}, 35%, 38%)` }
})

// If the avatar image fails to load, fall back to the initials gradient instead
// of letting the browser show a broken-image icon. Reset whenever the URL changes
// so a new (possibly working) URL gets a retry.
const imageBroken = ref(false)
watch(
  () => props.user.avatarUrl,
  () => {
    imageBroken.value = false
  },
)
const showImage = computed(() => Boolean(props.user.avatarUrl) && !imageBroken.value)

function onImageError() {
  imageBroken.value = true
}
</script>

<template>
  <span
    class="inline-flex shrink-0 items-center justify-center overflow-hidden font-mono font-semibold tracking-tighter text-white"
    :style="{
      width: `${size}px`,
      height: `${size}px`,
      fontSize: `${fontSize}px`,
      ...(showImage ? {} : bgStyle),
    }"
  >
    <img
      v-if="showImage"
      :src="user.avatarUrl!"
      :alt="user.username"
      class="h-full w-full object-cover"
      @error="onImageError"
    />
    <template v-else>{{ initials }}</template>
  </span>
</template>
