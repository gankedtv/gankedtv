<script setup lang="ts">
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'
import ThemePicker from './ThemePicker.vue'
import UserAvatar from './UserAvatar.vue'
import IconSearch from './icons/IconSearch.vue'
import IconSun from './icons/IconSun.vue'
import IconMoon from './icons/IconMoon.vue'
import IconPlus from './icons/IconPlus.vue'

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
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary max-tablet:hidden"
          :active-class="navLinkActive"
        >
          Games
        </RouterLink>
        <RouterLink
          to="/trending"
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary max-tablet:hidden"
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
        <IconSearch :size="14" :stroke-width="2.2" class="shrink-0" />
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
          :aria-label="theme.isDark ? 'Switch to light mode' : 'Switch to dark mode'"
          :aria-pressed="!theme.isDark"
          @click="theme.toggle()"
        >
          <IconSun v-if="theme.isDark" :size="16" />
          <IconMoon v-else :size="16" />
        </button>

        <!-- Upload button -->
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="inline-flex h-9 cursor-pointer items-center rounded-md bg-brand px-4 text-[13px] font-semibold uppercase tracking-[0.02em] text-white no-underline transition-colors duration-150 hover:bg-brand-light"
        >
          <span class="inline-flex items-center gap-1.5">
            <IconPlus :size="12" :stroke-width="2.5" />
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
          <UserAvatar :user="auth.user" :size="36" />
        </RouterLink>
      </div>
    </div>
  </header>
</template>
