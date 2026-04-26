<script setup lang="ts">
import { ref, computed, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { USERS, CLIPS, GAMES, formatNum, userByUsername } from '@/lib/mock-data'
import UserAvatar from '@/components/UserAvatar.vue'
import ClipCard from '@/components/ClipCard.vue'
import IconVerifiedBadge from '@/components/icons/IconVerifiedBadge.vue'
import IconShare from '@/components/icons/IconShare.vue'
import IconMoreHorizontal from '@/components/icons/IconMoreHorizontal.vue'

const route = useRoute()
const router = useRouter()

const resolved = computed(() => userByUsername(route.params.username as string))

watchEffect(() => {
  // Surface unknown usernames as a real 404 instead of silently rendering a placeholder.
  if (route.params.username && resolved.value === undefined) {
    router.replace({ name: 'not-found' })
  }
})

const userKey = computed(() => resolved.value?.[0] ?? '')
const user = computed(() => resolved.value?.[1])

const bannerGradient = computed(() => {
  const avatar = user.value?.avatar ?? '#000'
  return `linear-gradient(135deg, ${avatar}, color-mix(in oklab, ${avatar} 20%, #000))`
})

const avatarGradient = computed(() => {
  const avatar = user.value?.avatar ?? '#000'
  return `linear-gradient(135deg, ${avatar}, color-mix(in oklab, ${avatar} 20%, #000))`
})

const initials = computed(
  () =>
    (user.value?.display ?? '')
      .replace(/[^a-zA-Z]/g, '')
      .slice(0, 2)
      .toUpperCase() || '??',
)

const userClips = computed(() => CLIPS.filter((c) => c.user === userKey.value))

// Aggregate stats derived from clips
const totalPlays = computed(() => userClips.value.reduce((s, c) => s + c.views, 0))
const totalLikes = computed(() => userClips.value.reduce((s, c) => s + c.likes, 0))
const avgLikes = computed(() =>
  userClips.value.length ? Math.round(totalLikes.value / userClips.value.length) : 0,
)

// Games the user has clips in
const userGameKeys = computed(() => {
  const seen = new Set<string>()
  for (const c of userClips.value) {
    if (!seen.has(c.game)) seen.add(c.game)
  }
  return [...seen].slice(0, 5)
})

const gameClipCount = computed(() => {
  const counts: Record<string, number> = {}
  for (const c of userClips.value) {
    counts[c.game] = (counts[c.game] ?? 0) + 1
  }
  return counts
})

type Tab = 'clips' | 'liked' | 'about' | 'followers'
const tab = ref<Tab>('clips')

const following = ref(false)

const TABS: { key: Tab; label: string }[] = [
  { key: 'clips', label: 'Clips' },
  { key: 'liked', label: 'Liked' },
  { key: 'about', label: 'About' },
  { key: 'followers', label: 'Followers' },
]

const sort = ref('recent')

// Follower cards — other users
const followerUsers = computed(() =>
  Object.entries(USERS)
    .filter(([k]) => k !== userKey.value)
    .slice(0, 8),
)

// Local follow toggle state (mock — no API yet)
const followedUsers = ref(new Set<string>())
function toggleFollowedUser(key: string) {
  if (followedUsers.value.has(key)) followedUsers.value.delete(key)
  else followedUsers.value.add(key)
  // Force reactivity for Set mutations
  followedUsers.value = new Set(followedUsers.value)
}

const joinedDate = 'Jan 2024'
</script>

<template>
  <main v-if="user" class="relative">
    <!-- ===================== BANNER ===================== -->
    <div class="relative h-70 overflow-hidden" :style="{ background: bannerGradient }">
      <!-- Stripe texture -->
      <div
        class="absolute inset-0 bg-[repeating-linear-gradient(45deg,rgba(255,255,255,0.04)_0_12px,transparent_12px_24px)]"
      ></div>
      <!-- Fade to base at bottom -->
      <div
        class="absolute inset-0 bg-[linear-gradient(0deg,var(--color-surface-base),transparent_60%)]"
      ></div>

      <!-- Breadcrumb -->
      <div class="absolute top-6 right-0 left-0">
        <div class="mx-auto max-w-7xl px-6">
          <button
            class="flex cursor-pointer items-center gap-1.5 border-none bg-transparent p-0 font-mono text-[11px] uppercase tracking-[0.08em] text-white/55"
            @click="router.push({ name: 'home' })"
          >
            ← Feed / @{{ user.username }}
          </button>
        </div>
      </div>
    </div>

    <!-- ===================== INNER CONTENT ===================== -->
    <div class="mx-auto max-w-7xl px-6 pb-30">
      <!-- ---- Profile header ---- -->
      <div class="-mt-17.5 flex flex-wrap items-start gap-7">
        <!-- Large avatar -->
        <div
          class="flex h-35 w-35 shrink-0 select-none items-center justify-center rounded-full border-4 border-surface-base font-heading text-[56px] font-bold tracking-[-0.02em] text-white"
          :style="{ background: avatarGradient }"
        >
          {{ initials }}
        </div>

        <!-- User info -->
        <div class="min-w-55 flex-1 pt-19">
          <!-- Eyebrow -->
          <div class="mb-1.5 font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted">
            {{ user.verified ? 'Verified Creator / Player' : 'Player' }} · Joined {{ joinedDate }}
          </div>

          <!-- Display name + verified badge -->
          <div class="flex flex-wrap items-center gap-2.5">
            <h1
              class="m-0 font-heading text-[44px] font-bold leading-none uppercase tracking-[0.02em] text-text-primary"
            >
              {{ user.display }}
            </h1>
            <IconVerifiedBadge v-if="user.verified" :size="22" class="mt-1 shrink-0" />
          </div>

          <!-- Handle -->
          <div class="mt-1.5 font-mono text-sm tracking-[0.04em] text-neon">
            @{{ user.username }}
          </div>

          <!-- Bio -->
          <p class="m-0 mt-2.5 max-w-130 text-sm leading-[1.55] text-text-secondary">
            Grinding the ranked ladder one clip at a time. Content creator &amp; full-time gamer.
            Clips, vods, and the occasional tutorial.
          </p>
        </div>

        <!-- Action buttons -->
        <div class="flex flex-wrap items-center gap-2 pt-19">
          <!-- Follow / Following -->
          <button
            :class="[
              'cursor-pointer rounded-sm px-5.5 py-2.25 font-mono text-[11px] uppercase tracking-widest transition-all duration-150',
              following
                ? 'border border-border-strong bg-transparent text-text-primary'
                : 'border border-transparent bg-brand text-white',
            ]"
            @click="following = !following"
          >
            {{ following ? 'Following' : 'Follow' }}
          </button>

          <!-- Share -->
          <button
            class="flex h-9 w-9 cursor-pointer items-center justify-center rounded-sm border border-border bg-surface-raised text-text-secondary transition-[border-color] duration-150 hover:border-border-hover"
            aria-label="Share profile"
          >
            <IconShare :size="14" />
          </button>

          <!-- More -->
          <button
            class="flex h-9 w-9 cursor-pointer items-center justify-center rounded-sm border border-border bg-surface-raised text-text-secondary transition-[border-color] duration-150 hover:border-border-hover"
            aria-label="More options"
          >
            <IconMoreHorizontal :size="14" />
          </button>
        </div>
      </div>

      <!-- ---- Stat block ---- -->
      <div
        class="mt-7 grid grid-cols-[repeat(auto-fit,minmax(140px,1fr))] gap-px overflow-hidden rounded-md border border-border bg-border"
      >
        <div
          v-for="stat in [
            { label: 'Clips', value: formatNum(userClips.length) },
            { label: 'Followers', value: formatNum(user.followers) },
            { label: 'Following', value: '284' },
            { label: 'Total plays', value: formatNum(totalPlays) },
            { label: 'Total likes', value: formatNum(totalLikes) },
            { label: 'Avg / clip', value: formatNum(avgLikes) },
          ]"
          :key="stat.label"
          class="flex flex-col gap-1 bg-surface-raised px-5 py-4"
        >
          <span class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">{{
            stat.label
          }}</span>
          <span class="font-heading text-[22px] font-bold leading-none text-text-primary">{{
            stat.value
          }}</span>
        </div>
      </div>

      <!-- ---- Main arsenal ---- -->
      <div class="mt-5 rounded-md border border-border bg-surface-raised px-5 py-4">
        <div class="mb-3.5 font-mono text-[10px] uppercase tracking-widest text-text-muted">
          Main Arsenal
        </div>
        <div class="flex flex-wrap gap-2.5">
          <div
            v-for="gk in userGameKeys"
            :key="gk"
            class="flex items-center gap-2.25 rounded-full border border-border bg-surface-overlay py-1.5 pr-3.5 pl-1.5"
          >
            <!-- Game circle thumb -->
            <div
              class="flex h-7 w-7 shrink-0 items-center justify-center overflow-hidden rounded-full font-mono text-[9px] font-bold tracking-[0.04em] text-white"
              :style="{ backgroundImage: `url(${GAMES[gk].art})`, backgroundSize: 'cover' }"
            ></div>
            <div class="flex flex-col gap-px leading-none">
              <span class="font-mono text-[11px] font-medium text-text-primary">{{
                GAMES[gk].name
              }}</span>
              <span class="font-mono text-[10px] text-text-muted"
                >{{ gameClipCount[gk] }} clips</span
              >
            </div>
          </div>
        </div>
      </div>

      <!-- ---- Tabs ---- -->
      <div class="mt-9">
        <!-- Tab bar -->
        <div class="flex items-center border-b border-border">
          <div class="flex flex-1 gap-0">
            <button
              v-for="t in TABS"
              :key="t.key"
              :class="[
                'relative cursor-pointer border-none bg-transparent px-4.5 py-3 font-mono text-xs uppercase tracking-[0.08em] transition-colors duration-150 hover:text-text-primary',
                tab === t.key
                  ? `text-text-primary after:absolute after:right-0 after:-bottom-px after:left-0 after:h-0.5 after:rounded-t-xs after:bg-brand-light after:content-['']`
                  : 'text-text-muted',
              ]"
              @click="tab = t.key"
            >
              {{ t.label }}
            </button>
          </div>

          <!-- Sort -->
          <div
            v-if="tab === 'clips'"
            class="flex items-center gap-2 pb-2 font-mono text-[11px] uppercase text-text-muted"
          >
            <span>Sort:</span>
            <select
              v-model="sort"
              aria-label="Sort clips"
              class="cursor-pointer rounded-sm border border-border bg-surface-raised px-2.5 py-1.25 font-mono text-[11px] text-text-primary outline-none focus-visible:border-brand focus-visible:ring-1 focus-visible:ring-brand"
            >
              <option value="recent">Recent</option>
              <option value="top">Top</option>
              <option value="views">Views</option>
            </select>
          </div>
        </div>

        <!-- Tab content -->
        <div class="mt-6">
          <!-- Clips tab -->
          <div v-if="tab === 'clips'">
            <div class="feed-grid">
              <ClipCard
                v-for="clip in userClips"
                :key="clip.id"
                :clip="clip"
                @click="router.push({ name: 'clip', params: { id: clip.id } })"
              />
            </div>
          </div>

          <!-- Liked tab -->
          <div v-else-if="tab === 'liked'" class="flex items-center justify-center py-20">
            <p class="font-mono text-[13px] tracking-[0.06em] text-text-muted">
              Liked clips are private.
            </p>
          </div>

          <!-- About tab -->
          <div v-else-if="tab === 'about'">
            <div class="grid grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-3">
              <div
                v-for="card in [
                  { label: 'Peak Rank', value: 'Diamond I', icon: '◆' },
                  { label: 'Preferred Role', value: 'Duelist / Entry', icon: '⚡' },
                  { label: 'Socials', value: 'twitch · youtube · x', icon: '🔗' },
                  { label: 'Rig', value: 'RTX 4080 · i9-13900K', icon: '🖥' },
                  { label: 'Joined', value: joinedDate, icon: '📅' },
                  { label: 'Region', value: 'NA East', icon: '🌎' },
                ]"
                :key="card.label"
                class="flex flex-col gap-2 rounded-md border border-border bg-surface-raised px-6 py-5"
              >
                <div class="text-xl leading-none">{{ card.icon }}</div>
                <div class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">
                  {{ card.label }}
                </div>
                <div class="font-heading text-lg font-bold leading-[1.2] text-text-primary">
                  {{ card.value }}
                </div>
              </div>
            </div>
          </div>

          <!-- Followers tab -->
          <div v-else-if="tab === 'followers'">
            <div class="grid grid-cols-[repeat(auto-fill,minmax(160px,1fr))] gap-3">
              <div
                v-for="[fk, fu] in followerUsers"
                :key="fk"
                class="flex flex-col items-center gap-2.5 rounded-md border border-border bg-surface-raised p-5 text-center"
              >
                <button
                  class="cursor-pointer border-none bg-transparent p-0"
                  :aria-label="`Open ${fu.display}'s profile`"
                  @click="router.push({ name: 'user', params: { username: fu.username } })"
                >
                  <UserAvatar :user="fk" :size="52" />
                </button>
                <div class="flex flex-col gap-0.75">
                  <button
                    class="cursor-pointer border-none bg-transparent p-0 font-heading text-base font-bold uppercase tracking-[0.04em] text-text-primary"
                    @click="router.push({ name: 'user', params: { username: fu.username } })"
                  >
                    {{ fu.display }}
                  </button>
                  <span class="font-mono text-[11px] text-neon">@{{ fu.username }}</span>
                </div>
                <button
                  class="mt-1 cursor-pointer rounded-sm border-none px-4.5 py-1.5 font-mono text-[10px] uppercase tracking-widest transition-colors duration-150"
                  :class="
                    followedUsers.has(fk)
                      ? 'border border-border-strong bg-transparent text-text-primary'
                      : 'bg-brand text-white hover:bg-brand-light'
                  "
                  @click="toggleFollowedUser(fk)"
                >
                  {{ followedUsers.has(fk) ? 'Following' : 'Follow' }}
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </main>
</template>
