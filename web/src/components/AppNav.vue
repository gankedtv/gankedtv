<script setup lang="ts">
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'
import ThemePicker from './ThemePicker.vue'
import UserAvatar from './UserAvatar.vue'

const auth = useAuthStore()
const theme = useThemeStore()

const navLinkActive =
  "text-text-primary after:content-[''] after:absolute after:left-3.5 after:right-3.5 after:bottom-0.5 after:h-0.5 after:bg-brand-light"
</script>

<template>
  <header
    class="sticky top-0 z-50 h-16 border-b border-border bg-[color-mix(in_oklab,var(--color-surface-base)_85%,transparent)] backdrop-blur-[14px]"
  >
    <div class="mx-auto flex h-full max-w-360 min-w-0 items-center gap-5 px-6 *:shrink-0">
      <!-- Logo -->
      <RouterLink
        to="/"
        class="flex items-center gap-2 font-display text-[22px] font-bold uppercase tracking-[0.06em] text-text-primary no-underline"
      >
        <span class="logo__mark"></span>
        GANKED<span class="logo__tv">.TV</span>
      </RouterLink>

      <!-- Nav links -->
      <nav class="flex flex-1 items-center gap-1" aria-label="Main navigation">
        <RouterLink
          to="/"
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary"
          :exact-active-class="navLinkActive"
        >
          Feed
        </RouterLink>
        <RouterLink
          to="/games"
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary max-[720px]:hidden"
          :active-class="navLinkActive"
        >
          Games
        </RouterLink>
        <RouterLink
          to="/trending"
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary max-[720px]:hidden"
          :active-class="navLinkActive"
        >
          Trending
        </RouterLink>
      </nav>

      <!-- Search (desktop only, decorative) -->
      <div
        class="hidden h-9 w-60 max-w-60 min-w-0 shrink items-center gap-2 overflow-hidden rounded-md border border-border bg-surface-overlay px-3 font-mono text-xs whitespace-nowrap text-text-muted min-[1281px]:flex"
        aria-hidden="true"
      >
        <svg
          class="shrink-0"
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
        <span class="min-w-0 flex-1 truncate">search clips, players, games</span>
        <kbd class="shrink-0">⌘K</kbd>
      </div>

      <!-- Actions -->
      <div class="ml-auto flex items-center gap-2">
        <!-- Theme picker (Underground / Tactical / Arcade) -->
        <ThemePicker />

        <!-- Light/dark toggle -->
        <button
          class="inline-flex h-9 w-9 cursor-pointer items-center justify-center rounded-md border border-border bg-transparent text-text-secondary transition-all duration-150 hover:border-border-hover hover:text-text-primary"
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
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="inline-flex h-9 cursor-pointer items-center rounded-md bg-brand px-4 text-[13px] font-semibold uppercase tracking-[0.02em] text-white no-underline transition-colors duration-150 hover:bg-brand-light"
        >
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
            <span class="hidden min-[1041px]:inline">Upload</span>
          </span>
        </RouterLink>

        <!-- Sign in -->
        <RouterLink
          v-else
          to="/login"
          class="inline-flex h-9 cursor-pointer items-center rounded-md bg-brand px-4 text-[13px] font-semibold uppercase tracking-[0.02em] text-white no-underline transition-colors duration-150 hover:bg-brand-light"
        >
          Sign In
        </RouterLink>

        <!-- Avatar -->
        <RouterLink
          v-if="auth.isAuthenticated && auth.user"
          :to="`/user/${auth.user.username}`"
          class="inline-flex"
        >
          <UserAvatar :user="auth.user.username" :size="36" />
        </RouterLink>
      </div>
    </div>
  </header>
</template>
