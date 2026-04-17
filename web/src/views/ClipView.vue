<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()
const clipId = computed(() => {
  const id = route.params.id
  return Array.isArray(id) ? id[0] : id
})
</script>

<template>
  <div class="flex flex-col items-center justify-center min-h-[calc(100vh-4rem)] gap-3">
    <h1 class="font-heading text-3xl font-bold uppercase tracking-wide text-text-primary">Clip</h1>
    <p class="font-mono text-sm text-text-muted">{{ clipId }}</p>
  </div>
</template>
