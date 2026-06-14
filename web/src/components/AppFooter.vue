<script setup lang="ts">
import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { volIssMeta } from '@/lib/issue'

// Colophon footer — reads like a publication signoff, not a sitemap dump.
// Also the canonical home for Trending/Leaderboards links on phones, where
// the tab bar only carries the five primary destinations.
const auth = useAuthStore()

const accountLinks = computed(() =>
  auth.isAuthenticated
    ? [
        { label: 'Profile', to: `/user/${auth.user!.username}` },
        { label: 'Upload', to: '/upload' },
        { label: 'Notifications', to: '/notifications' },
        { label: 'Settings', to: '/settings/password' },
      ]
    : [
        { label: 'Sign in', to: '/login' },
        { label: 'Join the archive', to: '/register' },
      ],
)

const siteLinks = [
  { label: 'Feed', to: '/' },
  { label: 'Games', to: '/games' },
  { label: 'Trending', to: '/trending' },
  { label: 'Leaderboards', to: '/leaderboards' },
  { label: 'Reels', to: '/feed/reels' },
]
</script>

<template>
  <footer class="mt-20 border-t border-border">
    <div
      class="mx-auto grid max-w-[1440px] grid-cols-[1.6fr_1fr_1fr_1fr] gap-8 px-8 py-10 max-tablet:grid-cols-2 max-tablet:px-4"
    >
      <div class="max-tablet:col-span-2">
        <RouterLink to="/" class="mb-3.5 flex items-center gap-2.5">
          <span class="size-2 bg-ink" aria-hidden="true" />
          <span
            class="font-display text-[17px] font-bold uppercase tracking-[0.04em] text-text-primary"
          >
            GANKED<span class="text-ink">.TV</span>
          </span>
        </RouterLink>
        <p class="max-w-[30ch] text-[13px] leading-[1.55] text-text-secondary">
          An archive of gaming's loudest seconds. Every clip filed, numbered, and kept.
        </p>
      </div>

      <nav aria-label="Site">
        <h4 class="mb-3.5 font-mono text-[10px] uppercase tracking-[0.18em] text-text-muted">
          The Site
        </h4>
        <RouterLink
          v-for="link in siteLinks"
          :key="link.to"
          :to="link.to"
          class="mb-2 block text-[13px] text-text-secondary transition-colors duration-150 hover:text-ink"
        >
          {{ link.label }}
        </RouterLink>
      </nav>

      <nav aria-label="Account">
        <h4 class="mb-3.5 font-mono text-[10px] uppercase tracking-[0.18em] text-text-muted">
          Account
        </h4>
        <RouterLink
          v-for="link in accountLinks"
          :key="link.to"
          :to="link.to"
          class="mb-2 block text-[13px] text-text-secondary transition-colors duration-150 hover:text-ink"
        >
          {{ link.label }}
        </RouterLink>
      </nav>

      <nav aria-label="Off-site">
        <h4 class="mb-3.5 font-mono text-[10px] uppercase tracking-[0.18em] text-text-muted">
          Off-site
        </h4>
        <a
          href="https://github.com/gankedtv"
          rel="noopener"
          target="_blank"
          class="mb-2 block text-[13px] text-text-secondary transition-colors duration-150 hover:text-ink"
        >
          GitHub
        </a>
        <a
          href="https://discord.com"
          rel="noopener"
          target="_blank"
          class="mb-2 block text-[13px] text-text-secondary transition-colors duration-150 hover:text-ink"
        >
          Discord
        </a>
      </nav>
    </div>

    <div
      class="mx-auto flex max-w-[1440px] flex-wrap justify-between gap-4 border-t border-border px-8 py-4 font-mono text-[10px] uppercase tracking-[0.15em] text-text-muted max-tablet:px-4"
    >
      <span>{{ volIssMeta() }}</span>
      <span>© 2026 GANKED.TV</span>
    </div>
  </footer>
</template>
