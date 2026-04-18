<script setup lang="ts">
import { useRoute } from 'vue-router'
const route = useRoute()
</script>

<template>
  <div class="flex flex-col items-center justify-center min-h-[calc(100vh-4rem)] gap-3">
    <h1 class="font-heading text-3xl font-bold uppercase tracking-wide text-text-primary">
      Profile
    </h1>
    <p class="font-mono text-sm text-neon">@{{ route.params.username }}</p>
  </div>
</template>
