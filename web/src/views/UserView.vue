<script setup lang="ts">
import { useRoute } from 'vue-router'
const route = useRoute()
</script>

<template>
  <div class="view-stub">
    <h1 class="view-stub__title">Profile</h1>
    <p class="view-stub__sub view-stub__sub--mono">@{{ route.params.username }}</p>
  </div>
</template>

<style scoped>
.view-stub {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  min-height: calc(100vh - 64px);
  gap: 0.75rem;
}

.view-stub__title {
  font-family: 'Barlow Condensed', sans-serif;
  font-size: 3rem;
  font-weight: 700;
  color: var(--color-text-primary);
  letter-spacing: 0.02em;
  text-transform: uppercase;
}

.view-stub__sub {
  font-family: 'DM Sans', sans-serif;
  font-size: 1rem;
  color: var(--color-text-secondary);
}

.view-stub__sub--mono {
  font-family: 'DM Mono', monospace;
  font-size: 0.875rem;
  color: var(--color-neon);
}
</style>
