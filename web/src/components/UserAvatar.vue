<script setup lang="ts">
import { computed } from 'vue'
import { USERS } from '@/lib/mock-data'

const props = withDefaults(
  defineProps<{
    user: string
    size?: number
  }>(),
  { size: 32 },
)

const userData = computed(() => USERS[props.user] ?? { display: props.user, avatar: '#6d28d9' })

const initials = computed(() => {
  return (
    userData.value.display
      .replace(/[^a-zA-Z]/g, '')
      .slice(0, 2)
      .toUpperCase() || '??'
  )
})

const fontSize = computed(() => Math.floor(props.size * 0.35))

const bgStyle = computed(() => ({
  background: `linear-gradient(135deg, ${userData.value.avatar}, color-mix(in oklab, ${userData.value.avatar} 40%, #000))`,
}))
</script>

<template>
  <span
    class="inline-flex shrink-0 items-center justify-center overflow-hidden rounded-full font-mono font-semibold tracking-tighter text-white"
    :style="{
      width: `${size}px`,
      height: `${size}px`,
      fontSize: `${fontSize}px`,
      ...bgStyle,
    }"
  >
    {{ initials }}
  </span>
</template>
