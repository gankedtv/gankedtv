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
  return userData.value.display
    .replace(/[^a-zA-Z]/g, '')
    .slice(0, 2)
    .toUpperCase() || '??'
})

const fontSize = computed(() => Math.floor(props.size * 0.35))

const bgStyle = computed(() => ({
  background: `linear-gradient(135deg, ${userData.value.avatar}, color-mix(in oklab, ${userData.value.avatar} 40%, #000))`,
}))
</script>

<template>
  <span
    class="avatar shrink-0 inline-flex items-center justify-center rounded-full overflow-hidden"
    :style="{
      width: `${size}px`,
      height: `${size}px`,
      fontFamily: 'var(--font-mono)',
      fontWeight: 600,
      fontSize: `${fontSize}px`,
      color: '#fff',
      letterSpacing: '-0.03em',
      ...bgStyle,
    }"
  >
    {{ initials }}
  </span>
</template>
