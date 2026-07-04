<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import AppNav from '@/components/AppNav.vue'
import AppFooter from '@/components/AppFooter.vue'
import MobileTabBar from '@/components/MobileTabBar.vue'

// Reels is a full-bleed watch surface — the colophon would poke out under the
// fixed viewport, so it's the one route without a footer.
const route = useRoute()
const showFooter = computed(() => route.name !== 'reels' && route.name !== 'reel-clip')
</script>

<template>
  <div class="min-h-screen pb-15.5 lg:pb-0">
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
    <AppFooter v-if="showFooter" />
    <MobileTabBar />
  </div>
</template>
