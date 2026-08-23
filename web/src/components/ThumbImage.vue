<script setup lang="ts">
import { ref, onMounted, useTemplateRef, watch } from 'vue'

// The one way to render a clip thumbnail or game cover. Centralised because the loading
// behaviour — lazy below the fold, async decode, fade in over the placeholder instead of
// popping out of a black box — has to be identical everywhere or the feed looks uneven.
//
// Renders only the <img>; call sites keep their own aspect-ratio wrapper and overlays, and
// their `class` merges onto the element as usual.
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
watch(() => props.src, () => {
  loaded.value = false
  // The new src may also come from cache.
  requestAnimationFrame(settleIfReady)
})
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
