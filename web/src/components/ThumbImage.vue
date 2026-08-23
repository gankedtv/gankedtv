<script setup lang="ts">
import { ref, onMounted, useTemplateRef, watch } from 'vue'

// The one way to render a thumbnail or cover. Renders only the <img>; call sites keep their
// own wrapper and overlays.
const props = withDefaults(
  defineProps<{
    src: string
    alt?: string
    /** For the LCP element only — the home hero, the trending feature, a game's cover. */
    eager?: boolean
  }>(),
  { alt: '', eager: false },
)

const el = useTemplateRef<HTMLImageElement>('el')
const loaded = ref(false)

// A cached image is already complete before `load` fires; fading it in would be a flash.
function settleIfReady() {
  if (el.value?.complete) loaded.value = true
}

onMounted(settleIfReady)
watch(
  () => props.src,
  () => {
    loaded.value = false
    requestAnimationFrame(settleIfReady)
  },
)
</script>

<template>
  <img
    ref="el"
    :src="props.src"
    :alt="props.alt"
    :loading="props.eager ? 'eager' : 'lazy'"
    :fetchpriority="props.eager ? 'high' : 'auto'"
    decoding="async"
    class="transition-opacity duration-150"
    :class="loaded ? 'opacity-100' : 'opacity-0'"
    @load="loaded = true"
    @error="loaded = true"
  />
</template>
