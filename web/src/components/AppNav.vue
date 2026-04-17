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
          class="nav-link"
          active-class="nav-link--active"
          :exact="true"
        >
          Home
        </RouterLink>
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="nav-link"
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

<style scoped>
/* Nav links need a <style> block only for ::after pseudo-element on active state */
.nav-link {
  position: relative;
  padding: 0.375rem 0.75rem;
  border-radius: 0.375rem;
  font-size: 0.875rem;
  font-weight: 500;
  color: var(--color-text-secondary);
  text-decoration: none;
  transition: color 0.15s ease, background-color 0.15s ease;
}

.nav-link:hover {
  color: var(--color-text-primary);
  background-color: var(--color-surface-overlay);
}

.nav-link--active {
  color: var(--color-brand-light);
}

.nav-link--active::after {
  content: '';
  position: absolute;
  bottom: -2px;
  left: 0.75rem;
  right: 0.75rem;
  height: 2px;
  background: var(--color-brand-light);
  border-radius: 1px;
}
</style>
