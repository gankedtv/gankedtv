<script setup lang="ts">
import AppNav from '@/components/AppNav.vue'
</script>

<template>
  <div class="min-h-screen">
    <AppNav />
    <RouterView v-slot="{ Component }">
      <Transition
        mode="out-in"
        enter-active-class="transition-opacity duration-150 ease-[ease]"
        leave-active-class="transition-opacity duration-150 ease-[ease]"
        enter-from-class="opacity-0"
        leave-to-class="opacity-0"
      >
        <component :is="Component" />
      </Transition>
    </RouterView>
  </div>
</template>
