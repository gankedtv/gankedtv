<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { RouterLink } from 'vue-router'
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
  <header class="nav" :class="{ 'nav--scrolled': scrolled }">
    <div class="nav__inner">
      <!-- Logo -->
      <RouterLink to="/" class="nav__logo">
        GANKED<span class="nav__logo-accent">.TV</span>
      </RouterLink>

      <!-- Center nav links (desktop) -->
      <nav class="nav__links" aria-label="Main navigation">
        <RouterLink to="/" class="nav__link" active-class="nav__link--active" exact>
          Home
        </RouterLink>
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="nav__link"
          active-class="nav__link--active"
        >
          Upload
        </RouterLink>
      </nav>

      <!-- Right actions -->
      <div class="nav__actions">
        <ThemeToggle />
        <RouterLink
          v-if="!auth.isAuthenticated"
          to="/login"
          class="nav__signin"
        >
          Sign In
        </RouterLink>
        <span v-else class="nav__user">
          {{ auth.user?.username }}
        </span>
      </div>
    </div>
  </header>
</template>

<style scoped>
.nav {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  z-index: 50;
  height: 64px;
  background: color-mix(in srgb, var(--color-surface-base) 90%, transparent);
  backdrop-filter: blur(12px);
  -webkit-backdrop-filter: blur(12px);
  border-bottom: 1px solid transparent;
  transition: border-color 0.2s ease;
}

.nav--scrolled {
  border-bottom-color: var(--color-border);
}

.nav__inner {
  max-width: 1280px;
  margin: 0 auto;
  height: 100%;
  padding: 0 1.5rem;
  display: flex;
  align-items: center;
  gap: 2rem;
}

.nav__logo {
  font-family: var(--font-display);
  font-size: 1.375rem;
  font-weight: 700;
  letter-spacing: 0.05em;
  text-decoration: none;
  color: var(--color-text-primary);
  text-transform: uppercase;
  flex-shrink: 0;
}

.nav__logo-accent {
  color: var(--color-brand-light);
}

.nav__links {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  flex: 1;
  justify-content: center;
}

.nav__link {
  font-family: var(--font-body);
  font-size: 0.875rem;
  font-weight: 500;
  color: var(--color-text-secondary);
  text-decoration: none;
  padding: 0.375rem 0.75rem;
  border-radius: 0.375rem;
  transition: color 0.15s ease, background 0.15s ease;
  position: relative;
}

.nav__link:hover {
  color: var(--color-text-primary);
  background: var(--color-surface-overlay);
}

.nav__link--active {
  color: var(--color-brand-light);
}

.nav__link--active::after {
  content: '';
  position: absolute;
  bottom: -2px;
  left: 0.75rem;
  right: 0.75rem;
  height: 2px;
  background: var(--color-brand-light);
  border-radius: 1px;
}

.nav__actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-left: auto;
  flex-shrink: 0;
}

.nav__signin {
  font-family: var(--font-body);
  font-size: 0.875rem;
  font-weight: 500;
  color: var(--color-text-primary);
  background: var(--color-brand);
  text-decoration: none;
  padding: 0.375rem 1rem;
  border-radius: 0.375rem;
  transition: background 0.15s ease;
  white-space: nowrap;
}

.nav__signin:hover {
  background: var(--color-brand-light);
}

.nav__user {
  font-family: var(--font-mono);
  font-size: 0.8125rem;
  color: var(--color-text-secondary);
}

@media (max-width: 640px) {
  .nav__links {
    display: none;
  }
}
</style>
