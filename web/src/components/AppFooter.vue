<script setup lang="ts">
import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'
import LogoMark from './LogoMark.vue'

// Quiet footer — wordmark, one-liner, and link columns. Also the canonical
// home for Trending/Leaderboards links on phones, where the tab bar only
// carries the five primary destinations.
const auth = useAuthStore()
const currentYear = new Date().getFullYear()

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
        { label: 'Create account', to: '/register' },
      ],
)

const siteLinks = [
  { label: 'Home', to: '/' },
  { label: 'Games', to: '/games' },
  { label: 'Trending', to: '/trending' },
  { label: 'Leaderboards', to: '/leaderboards' },
  { label: 'Reels', to: '/feed/reels' },
]
</script>

<template>
  <footer class="mt-20 border-t border-border">
    <div
      class="mx-auto grid max-w-300 grid-cols-[1.6fr_1fr_1fr_1fr] gap-8 px-7 py-10 max-tablet:grid-cols-2 max-tablet:px-4"
    >
      <div class="max-tablet:col-span-2">
        <RouterLink to="/" class="mb-3.5 flex items-center gap-2">
          <LogoMark :size="20" />
          <span
            class="font-condensed text-[17px] font-black uppercase tracking-[0.04em] text-text-primary"
          >
            GANKED<span class="text-accent">.TV</span>
          </span>
        </RouterLink>
        <p class="max-w-[30ch] text-[13px] leading-[1.55] text-text-secondary">
          The home for gaming's loudest seconds. Clip it, share it, climb the chart.
        </p>
      </div>

      <nav aria-label="Site">
        <h4 class="mb-3.5 text-[10px] font-bold uppercase tracking-[0.14em] text-text-muted">
          Site
        </h4>
        <RouterLink
          v-for="link in siteLinks"
          :key="link.to"
          :to="link.to"
          class="mb-2 block text-[13px] text-text-secondary transition-colors duration-150 hover:text-accent"
        >
          {{ link.label }}
        </RouterLink>
      </nav>

      <nav aria-label="Account">
        <h4 class="mb-3.5 text-[10px] font-bold uppercase tracking-[0.14em] text-text-muted">
          Account
        </h4>
        <RouterLink
          v-for="link in accountLinks"
          :key="link.to"
          :to="link.to"
          class="mb-2 block text-[13px] text-text-secondary transition-colors duration-150 hover:text-accent"
        >
          {{ link.label }}
        </RouterLink>
      </nav>

      <nav aria-label="Off-site">
        <h4 class="mb-3.5 text-[10px] font-bold uppercase tracking-[0.14em] text-text-muted">
          Off-site
        </h4>
        <a
          href="https://github.com/gankedtv"
          rel="noopener"
          target="_blank"
          class="mb-2 block text-[13px] text-text-secondary transition-colors duration-150 hover:text-accent"
        >
          GitHub
        </a>
        <a
          href="https://discord.com"
          rel="noopener"
          target="_blank"
          class="mb-2 block text-[13px] text-text-secondary transition-colors duration-150 hover:text-accent"
        >
          Discord
        </a>
      </nav>
    </div>

    <div
      class="mx-auto flex max-w-300 flex-wrap justify-between gap-4 border-t border-border px-7 py-4 text-[11px] text-text-muted max-tablet:px-4"
    >
      <span>Made for players, by players.</span>
      <span>© {{ currentYear }} GankedTV</span>
    </div>
  </footer>
</template>
