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
  <!-- Literal colors — text over video must stay light in both modes. -->
  <span
    v-if="text"
    class="rounded-sm bg-black/75 font-semibold leading-none text-[#f4f1e8]"
    :class="size === 'md' ? 'px-2 py-1 text-[11px]' : 'px-1.5 py-1 text-[10px]'"
  >
    {{ text }}
  </span>
</template>
