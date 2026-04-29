<script setup lang="ts">
import { computed } from 'vue'
import { formatDuration } from '@/lib/format'

// `seconds` is null when the clip's duration hasn't been computed yet (e.g.
// uploaded but pre-thumbnail extraction). The badge renders nothing in that
// case so callers don't have to guard with `v-if`.
const props = withDefaults(
  defineProps<{
    seconds: number | null
    size?: 'sm' | 'md'
  }>(),
  { size: 'sm' },
)

const text = computed(() => (props.seconds === null ? null : formatDuration(props.seconds)))
</script>

<template>
  <span
    v-if="text"
    class="rounded-[3px] bg-black/75 font-mono leading-none text-white backdrop-blur-xs"
    :class="
      size === 'md'
        ? 'px-2.5 py-1.25 text-[11px] tracking-[0.06em]'
        : 'px-1.5 py-1 text-[10px] tracking-wider'
    "
  >
    {{ text }}
  </span>
</template>
