<script setup lang="ts">
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'
import ThemePicker from './ThemePicker.vue'
import UserAvatar from './UserAvatar.vue'

const auth = useAuthStore()
const theme = useThemeStore()
</script>

<template>
  <header class="nav">
    <div class="nav__inner">
      <!-- Logo -->
      <RouterLink to="/" class="logo">
        <span class="logo__mark"></span>
        GANKED<span class="logo__tv">.TV</span>
      </RouterLink>

      <!-- Nav links -->
      <nav class="nav__links" aria-label="Main navigation">
        <RouterLink to="/" class="nav__link" exact-active-class="nav__link--active"
          >Feed</RouterLink
        >
        <RouterLink
          to="/games"
          class="nav__link nav__link--hide-mobile"
          active-class="nav__link--active"
          >Games</RouterLink
        >
        <RouterLink
          to="/trending"
          class="nav__link nav__link--hide-mobile"
          active-class="nav__link--active"
          >Trending</RouterLink
        >
      </nav>

      <!-- Search (desktop only, decorative) -->
      <div class="nav__search" aria-hidden="true">
        <svg
          width="14"
          height="14"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2.2"
        >
          <circle cx="11" cy="11" r="7" />
          <path d="M21 21l-4.35-4.35" />
        </svg>
        <span>search clips, players, games</span>
        <kbd>⌘K</kbd>
      </div>

      <!-- Actions -->
      <div class="nav__actions">
        <!-- Theme picker (Underground / Tactical / Arcade) -->
        <ThemePicker />

        <!-- Light/dark toggle -->
        <button
          class="icon-btn"
          :title="theme.isDark ? 'Switch to light' : 'Switch to dark'"
          @click="theme.toggle()"
        >
          <svg
            v-if="theme.isDark"
            width="16"
            height="16"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
          >
            <circle cx="12" cy="12" r="4" />
            <path
              d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41"
            />
          </svg>
          <svg
            v-else
            width="16"
            height="16"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
          >
            <path d="M21 12.8A9 9 0 1111.2 3a7 7 0 009.8 9.8z" />
          </svg>
        </button>

        <!-- Upload button -->
        <RouterLink v-if="auth.isAuthenticated" to="/upload" class="btn-primary">
          <span class="inline-flex items-center gap-1.5">
            <svg
              width="12"
              height="12"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2.5"
            >
              <path d="M12 5v14M5 12h14" />
            </svg>
            <span class="upload-label">Upload</span>
          </span>
        </RouterLink>

        <!-- Sign in -->
        <RouterLink v-else to="/login" class="btn-primary">Sign In</RouterLink>

        <!-- Avatar -->
        <RouterLink v-if="auth.isAuthenticated" to="/user/phantomveil" class="inline-flex">
          <UserAvatar user="phantomveil" :size="36" />
        </RouterLink>
      </div>
    </div>
  </header>
</template>

<style scoped>
.nav {
  position: sticky;
  top: 0;
  z-index: 50;
  height: 64px;
  background: color-mix(in oklab, var(--color-surface-base) 85%, transparent);
  backdrop-filter: blur(14px);
  -webkit-backdrop-filter: blur(14px);
  border-bottom: 1px solid var(--color-border);
}

.nav__inner {
  max-width: 1440px;
  height: 100%;
  margin: 0 auto;
  padding: 0 24px;
  display: flex;
  align-items: center;
  gap: 20px;
  min-width: 0;
}

.nav__inner > * {
  flex-shrink: 0;
}

.logo {
  font-family: var(--font-display);
  font-weight: 700;
  font-size: 22px;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--color-text-primary);
  text-decoration: none;
}

.logo__tv {
  color: var(--color-brand-light);
}

[data-theme='tactical'] .logo__tv {
  color: var(--color-brand);
}

[data-theme='arcade'] .logo__tv {
  color: var(--color-neon);
}

.nav__links {
  display: flex;
  align-items: center;
  gap: 4px;
  flex: 1;
}

.nav__link {
  position: relative;
  padding: 8px 14px;
  font-size: 13px;
  font-weight: 500;
  color: var(--color-text-secondary);
  border-radius: var(--radius-sm);
  transition:
    color 150ms,
    background-color 150ms;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  text-decoration: none;
}

.nav__link:hover {
  color: var(--color-text-primary);
  background: var(--color-surface-overlay);
}

.nav__link--active {
  color: var(--color-text-primary);
}

.nav__link--active::after {
  content: '';
  position: absolute;
  left: 14px;
  right: 14px;
  bottom: 2px;
  height: 2px;
  background: var(--color-brand-light);
}

.nav__search {
  display: none;
  align-items: center;
  gap: 8px;
  height: 36px;
  padding: 0 12px;
  background: var(--color-surface-overlay);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  width: 240px;
  max-width: 240px;
  flex-shrink: 1;
  min-width: 0;
  font-family: var(--font-mono);
  font-size: 12px;
  color: var(--color-text-muted);
  white-space: nowrap;
  overflow: hidden;
}

.nav__search > span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
  flex: 1;
}

.nav__search svg,
.nav__search kbd {
  flex-shrink: 0;
}

@media (min-width: 1281px) {
  .nav__search {
    display: flex;
  }
}

.nav__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.icon-btn {
  width: 36px;
  height: 36px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: var(--radius-md);
  border: 1px solid var(--color-border);
  color: var(--color-text-secondary);
  background: transparent;
  transition: all 150ms;
  cursor: pointer;
}

.icon-btn:hover {
  border-color: var(--color-border-hover);
  color: var(--color-text-primary);
}

.btn-primary {
  height: 36px;
  padding: 0 16px;
  background: var(--color-brand);
  color: #fff;
  font-weight: 600;
  font-size: 13px;
  letter-spacing: 0.02em;
  border-radius: var(--radius-md);
  transition: background 150ms;
  display: inline-flex;
  align-items: center;
  text-decoration: none;
  border: none;
  cursor: pointer;
}

.btn-primary:hover {
  background: var(--color-brand-light);
}

@media (max-width: 1040px) {
  .upload-label {
    display: none;
  }
}

@media (max-width: 720px) {
  .nav__link--hide-mobile {
    display: none;
  }
}
</style>
