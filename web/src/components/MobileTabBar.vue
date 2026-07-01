<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import IconGrid from '@/components/icons/IconGrid.vue'
import IconGamepad from '@/components/icons/IconGamepad.vue'
import IconPlus from '@/components/icons/IconPlus.vue'
import IconReels from '@/components/icons/IconReels.vue'
import IconUser from '@/components/icons/IconUser.vue'

// Phone-first bottom tab bar — the five primary destinations. Replaces the
// hamburger drawer below lg; Trending/Leaderboards stay reachable via the
// footer and search. Upload is the prominent solid-mint button.
const route = useRoute()
const auth = useAuthStore()

const profileTo = computed(() => (auth.isAuthenticated ? `/user/${auth.user!.username}` : '/login'))
const uploadTo = computed(() =>
  auth.isAuthenticated ? '/upload' : { path: '/login', query: { redirect: '/upload' } },
)

const isProfileActive = computed(
  () =>
    route.name === 'user' && auth.isAuthenticated && route.params.username === auth.user!.username,
)

function tabClass(active: boolean): string {
  return [
    'relative flex flex-col items-center justify-center gap-1 text-[9px] font-semibold transition-colors duration-150',
    active ? 'text-accent' : 'text-text-muted',
  ].join(' ')
}
</script>

<template>
  <nav
    class="fixed inset-x-0 bottom-0 z-40 grid h-15.5 grid-cols-5 border-t border-border bg-surface-raised/95 pb-[env(safe-area-inset-bottom)] lg:hidden"
    aria-label="Primary"
  >
    <RouterLink to="/" :class="tabClass(route.name === 'home')">
      <IconGrid :size="19" />
      <span>Feed</span>
    </RouterLink>
    <RouterLink
      to="/games"
      :class="tabClass(route.name === 'games' || route.name === 'game-detail')"
    >
      <IconGamepad :size="19" />
      <span>Games</span>
    </RouterLink>
    <RouterLink :to="uploadTo" class="flex items-center justify-center" aria-label="Upload">
      <span class="flex size-9 items-center justify-center rounded-lg bg-accent text-[#080f0d]">
        <IconPlus :size="17" />
      </span>
    </RouterLink>
    <RouterLink
      to="/feed/reels"
      :class="tabClass(route.name === 'reels' || route.name === 'reel-clip')"
    >
      <IconReels :size="19" />
      <span>Reels</span>
    </RouterLink>
    <RouterLink :to="profileTo" :class="tabClass(isProfileActive)">
      <IconUser :size="19" />
      <span>You</span>
    </RouterLink>
  </nav>
</template>
