<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { useAuthStore } from '@/stores/auth'
import ThemeToggle from './ThemeToggle.vue'

const auth = useAuthStore()
const scrolled = ref(false)
const menuOpen = ref(false)

const navLinkClass =
  "relative rounded-md px-3 py-1.5 text-sm font-medium text-text-secondary no-underline transition-[color,background-color] duration-150 hover:bg-surface-overlay hover:text-text-primary after:absolute after:-bottom-0.5 after:left-3 after:right-3 after:h-0.5 after:rounded-[1px] after:bg-brand-light after:opacity-0 after:transition-opacity after:duration-150 after:content-[''] [&.nav-link--active]:text-brand-light [&.nav-link--active]:after:opacity-100"

function onScroll() {
  scrolled.value = window.scrollY > 20
}

onMounted(() => {
  window.addEventListener('scroll', onScroll, { passive: true })
  onScroll()
})
onUnmounted(() => window.removeEventListener('scroll', onScroll))
</script>

<template>
  <header
    class="fixed top-0 right-0 left-0 z-50 border-b bg-surface-base/90 backdrop-blur-md transition-colors duration-200"
    :class="scrolled ? 'border-border' : 'border-transparent'"
  >
    <div class="mx-auto flex h-16 max-w-7xl items-center gap-8 px-6">
      <RouterLink
        to="/"
        class="shrink-0 font-display text-[1.375rem] font-bold uppercase tracking-[0.05em] text-text-primary no-underline"
      >
        GANKED<span class="text-brand-light">.TV</span>
      </RouterLink>

      <nav
        class="hidden flex-1 items-center justify-center gap-1 sm:flex"
        aria-label="Main navigation"
      >
        <RouterLink to="/" :class="navLinkClass" exact-active-class="nav-link--active">
          Home
        </RouterLink>
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          :class="navLinkClass"
          active-class="nav-link--active"
        >
          Upload
        </RouterLink>
      </nav>

      <div class="ml-auto flex shrink-0 items-center gap-3">
        <ThemeToggle />
        <button
          class="rounded-md p-1.5 text-text-secondary transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary sm:hidden"
          :aria-expanded="menuOpen"
          aria-label="Toggle navigation"
          @click="menuOpen = !menuOpen"
        >
          <svg
            class="h-5 w-5"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            viewBox="0 0 24 24"
          >
            <path
              v-if="!menuOpen"
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M4 6h16M4 12h16M4 18h16"
            />
            <path v-else stroke-linecap="round" stroke-linejoin="round" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
        <RouterLink
          v-if="!auth.isAuthenticated"
          to="/login"
          class="rounded-md bg-brand px-4 py-1.5 text-sm font-medium text-text-primary no-underline transition-colors duration-150 hover:bg-brand-light"
        >
          Sign In
        </RouterLink>
        <template v-else>
          <span class="font-mono text-[0.8125rem] text-text-secondary">
            {{ auth.user?.username }}
          </span>
          <button
            class="font-mono text-xs text-text-muted transition-colors duration-150 hover:text-brand-light"
            @click="auth.logout()"
          >
            Sign out
          </button>
        </template>
      </div>
    </div>

    <div v-if="menuOpen" class="border-t border-border px-6 py-3 sm:hidden">
      <nav class="flex flex-col gap-1" aria-label="Mobile navigation">
        <RouterLink
          to="/"
          :class="navLinkClass"
          exact-active-class="nav-link--active"
          @click="menuOpen = false"
        >
          Home
        </RouterLink>
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          :class="navLinkClass"
          active-class="nav-link--active"
          @click="menuOpen = false"
        >
          Upload
        </RouterLink>
      </nav>
    </div>
  </header>
</template>
