<script setup lang="ts">
import { ref, onMounted, useTemplateRef, watch } from 'vue'

// The one way to render a clip thumbnail or game cover, so the loading behaviour is identical
// everywhere. Renders only the <img>; call sites keep their own wrapper and overlays.
const props = withDefaults(
  defineProps<{
    src: string
    alt?: string
    /**
     * Above-the-fold images (the home hero, the trending feature) load eagerly at high
     * priority — they are the LCP element, and deferring them is the opposite of the point.
     */
    eager?: boolean
  }>(),
  { alt: '', eager: false },
)

const el = useTemplateRef<HTMLImageElement>('el')
const loaded = ref(false)

// A cached image can already be complete before the load event would fire, and fading in
// something the browser has ready is a flash for no reason.
function settleIfReady() {
  if (el.value?.complete) loaded.value = true
}

onMounted(settleIfReady)
watch(
  () => props.src,
  () => {
    loaded.value = false
    // The new src may also come from cache.
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
