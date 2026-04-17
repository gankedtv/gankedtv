<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { useAuthStore } from '@/stores/auth'
import ThemeToggle from './ThemeToggle.vue'

const auth = useAuthStore()
const scrolled = ref(false)

function onScroll() {
  scrolled.value = window.scrollY > 20
}

onMounted(() => window.addEventListener('scroll', onScroll, { passive: true }))
onUnmounted(() => window.removeEventListener('scroll', onScroll))
</script>

<template>
  <header
    class="fixed top-0 right-0 left-0 z-50 h-16 border-b bg-surface-base/90 backdrop-blur-md transition-colors duration-200"
    :class="scrolled ? 'border-border' : 'border-transparent'"
  >
    <div class="mx-auto flex h-full max-w-7xl items-center gap-8 px-6">
      <RouterLink
        to="/"
        class="shrink-0 font-display text-[1.375rem] font-bold uppercase tracking-[0.05em] text-text-primary no-underline"
      >
        GANKED<span class="text-brand-light">.TV</span>
      </RouterLink>

      <nav class="flex flex-1 items-center justify-center gap-1 max-sm:hidden" aria-label="Main navigation">
        <RouterLink
          to="/"
          class="relative rounded-md px-3 py-1.5 text-sm font-medium text-text-secondary no-underline transition-[color,background-color] duration-150 hover:bg-surface-overlay hover:text-text-primary after:absolute after:-bottom-0.5 after:left-3 after:right-3 after:h-0.5 after:rounded-[1px] after:bg-brand-light after:opacity-0 after:transition-opacity after:duration-150 after:content-[''] [&.nav-link--active]:text-brand-light [&.nav-link--active]:after:opacity-100"
          active-class="nav-link--active"
          :exact="true"
        >
          Home
        </RouterLink>
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="relative rounded-md px-3 py-1.5 text-sm font-medium text-text-secondary no-underline transition-[color,background-color] duration-150 hover:bg-surface-overlay hover:text-text-primary after:absolute after:-bottom-0.5 after:left-3 after:right-3 after:h-0.5 after:rounded-[1px] after:bg-brand-light after:opacity-0 after:transition-opacity after:duration-150 after:content-[''] [&.nav-link--active]:text-brand-light [&.nav-link--active]:after:opacity-100"
          active-class="nav-link--active"
        >
          Upload
        </RouterLink>
      </nav>

      <div class="ml-auto flex shrink-0 items-center gap-3">
        <ThemeToggle />
        <RouterLink
          v-if="!auth.isAuthenticated"
          to="/login"
          class="rounded-md bg-brand px-4 py-1.5 text-sm font-medium text-text-primary no-underline transition-colors duration-150 hover:bg-brand-light"
        >
          Sign In
        </RouterLink>
        <span v-else class="font-mono text-[0.8125rem] text-text-secondary">
          {{ auth.user?.username }}
        </span>
      </div>
    </div>
  </header>
</template>
