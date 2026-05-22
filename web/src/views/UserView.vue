<script setup lang="ts">
import { ref, computed, watch, onBeforeUnmount } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { users, type UserProfile } from '@/api/users'
import { safeImageUrl } from '@/lib/url'
import { formatNum } from '@/lib/format'
import ClipCard from '@/components/ClipCard.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import IconShare from '@/components/icons/IconShare.vue'
import IconMoreHorizontal from '@/components/icons/IconMoreHorizontal.vue'

const route = useRoute()
const router = useRouter()

const profile = ref<UserProfile | null>(null)
const loading = ref(false)
const errored = ref(false)

const username = computed(() => {
  const u = route.params.username
  return Array.isArray(u) ? u[0] : u
})

// Monotonic counter — guards A→B→A races where comparing `username.value === name`
// would falsely accept the first A response after the second A request supersedes it.
let latestLoadId = 0

async function loadProfile(name: string) {
  const myLoadId = ++latestLoadId
  loading.value = true
  errored.value = false
  profile.value = null
  try {
    const result = await users.getByUsername(name)
    if (myLoadId !== latestLoadId) return
    profile.value = result
  } catch (err) {
    if (myLoadId !== latestLoadId) return
    if (err instanceof ApiError && err.status === 404) {
      router.replace({ name: 'not-found' })
      return
    }
    errored.value = true
  } finally {
    if (myLoadId === latestLoadId) loading.value = false
  }
}

watch(
  username,
  (name) => {
    if (!name) return
    loadProfile(name)
  },
  { immediate: true },
)

// Derive a stable avatar color from the username so the banner/avatar render
// even though the API doesn't return one yet (Phase 2 will store user-picked colors).
const avatarColor = computed(() => {
  const name = profile.value?.username ?? ''
  let hash = 0
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0
  return `hsl(${Math.abs(hash) % 360}, 65%, 45%)`
})

const bannerGradient = computed(
  () =>
    `linear-gradient(135deg, ${avatarColor.value}, color-mix(in oklab, ${avatarColor.value} 20%, #000))`,
)

const initials = computed(() => {
  const name = profile.value?.username ?? ''
  // Unicode-aware: keep letters/digits across scripts (Cyrillic, CJK, Hangul,
  // emoji-as-letter, etc.) and split on grapheme clusters so accented letters
  // and multi-codepoint glyphs count as one. The old `[^a-zA-Z]` strip would
  // hand a Korean or Cyrillic username an unhelpful `??` fallback.
  const letters = Array.from(name.normalize('NFC'))
    .filter((c) => /\p{L}|\p{N}/u.test(c))
    .slice(0, 2)
    .join('')
  return letters.toUpperCase() || '??'
})

// Hoisted so the template doesn't re-parse the URL on every render.
const avatarImageUrl = computed(() => safeImageUrl(profile.value?.avatarUrl))

const joinedDate = computed(() => {
  if (!profile.value) return ''
  return new Date(profile.value.createdAt).toLocaleString(undefined, {
    month: 'short',
    year: 'numeric',
  })
})

const totalPlays = computed(() => (profile.value?.clips ?? []).reduce((s, c) => s + c.viewCount, 0))
const totalLikes = computed(() => (profile.value?.clips ?? []).reduce((s, c) => s + c.likeCount, 0))

const copyMessage = ref<string | null>(null)
let copyTimer: ReturnType<typeof setTimeout> | null = null

async function copyShareUrl() {
  if (!navigator.clipboard) {
    copyMessage.value = 'Copy not supported'
  } else {
    try {
      await navigator.clipboard.writeText(window.location.href)
      copyMessage.value = 'Link copied'
    } catch {
      copyMessage.value = 'Copy failed'
    }
  }
  if (copyTimer !== null) clearTimeout(copyTimer)
  copyTimer = setTimeout(() => {
    copyMessage.value = null
  }, 1800)
}

onBeforeUnmount(() => {
  if (copyTimer !== null) clearTimeout(copyTimer)
})

type Tab = 'clips' | 'liked'
const tab = ref<Tab>('clips')

// Followers/Following/About tabs depend on social-graph endpoints that don't exist yet
// (Phase 3). Keep just Clips and Liked (Liked is a stub message).
const TABS: { key: Tab; label: string }[] = [
  { key: 'clips', label: 'Clips' },
  { key: 'liked', label: 'Liked' },
]
</script>

<template>
  <div>
    <!-- Single root so the route-level <Transition mode="out-in"> can animate the leave
         cleanly. This comment lives INSIDE the root <div> on purpose: a comment placed
         before the root element makes the component multi-root (comment + div), which
         <Transition> can't drive — its leave never resolves, so the next route's view
         never mounts (issue #92). The v-if chain below also has no bare v-else for the
         same reason — every branch is an element, never a fallthrough comment node. -->
    <main v-if="loading">
      <StatusPanel kind="loading" message="Loading…" />
    </main>

    <main v-else-if="errored">
      <StatusPanel kind="error" message="Couldn't load this profile.">
        <button
          class="cursor-pointer rounded-sm border border-border bg-surface-raised px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
          @click="username && loadProfile(username)"
        >
          Retry
        </button>
      </StatusPanel>
    </main>

    <main v-else-if="profile" class="relative">
      <!-- ===================== BANNER ===================== -->
      <div class="relative h-70 overflow-hidden" :style="{ background: bannerGradient }">
        <div
          class="absolute inset-0 bg-[repeating-linear-gradient(45deg,rgba(255,255,255,0.04)_0_12px,transparent_12px_24px)]"
        ></div>
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
              ← Feed / @{{ profile.username }}
            </button>
          </div>
        </div>
      </div>

      <!-- ===================== INNER CONTENT ===================== -->
      <div class="mx-auto max-w-7xl px-6 pb-30">
        <!-- ---- Profile header ---- -->
        <!-- relative + z-10 lifts the avatar above the banner's fade overlay so the
           negative-margin overlap actually paints on top instead of behind. -->
        <div class="relative z-10 -mt-17.5 flex flex-wrap items-start gap-7">
          <!-- Large avatar -->
          <div
            class="flex h-35 w-35 shrink-0 select-none items-center justify-center rounded-full border-4 border-surface-base font-heading text-[56px] font-bold tracking-[-0.02em] text-white"
            :style="{ background: bannerGradient }"
          >
            <img
              v-if="avatarImageUrl"
              :src="avatarImageUrl"
              :alt="profile.username"
              class="h-full w-full rounded-full object-cover"
            />
            <span v-else>{{ initials }}</span>
          </div>

          <!-- User info -->
          <div class="min-w-55 flex-1 pt-19">
            <div class="mb-1.5 font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted">
              Player · Joined {{ joinedDate }}
            </div>

            <div class="flex flex-wrap items-center gap-2.5">
              <h1
                class="m-0 font-heading text-[44px] font-bold leading-none uppercase tracking-[0.02em] text-text-primary"
              >
                {{ profile.username }}
              </h1>
            </div>

            <div class="mt-1.5 font-mono text-sm tracking-[0.04em] text-neon">
              @{{ profile.username }}
            </div>

            <p
              v-if="profile.bio"
              class="m-0 mt-2.5 max-w-130 text-sm leading-[1.55] text-text-secondary"
            >
              {{ profile.bio }}
            </p>
          </div>

          <!-- Action buttons (follow + share + more) -->
          <!-- Follow lives in Phase 3 (social-graph endpoints). Share is best-effort clipboard. -->
          <div class="flex flex-wrap items-center gap-2 pt-19">
            <button
              class="flex h-9 w-9 cursor-pointer items-center justify-center rounded-sm border border-border bg-surface-raised text-text-secondary transition-[border-color] duration-150 hover:border-border-hover"
              aria-label="Share profile"
              @click="copyShareUrl"
            >
              <IconShare :size="14" />
            </button>
            <button
              class="flex h-9 w-9 cursor-pointer items-center justify-center rounded-sm border border-border bg-surface-raised text-text-secondary transition-[border-color] duration-150 hover:border-border-hover"
              aria-label="More options"
            >
              <IconMoreHorizontal :size="14" />
            </button>
            <span
              v-if="copyMessage"
              aria-live="polite"
              class="font-mono text-[11px] uppercase tracking-widest text-neon"
            >
              {{ copyMessage }}
            </span>
          </div>
        </div>

        <!-- ---- Stat block ---- -->
        <div
          class="mt-7 grid grid-cols-[repeat(auto-fit,minmax(140px,1fr))] gap-px overflow-hidden rounded-md border border-border bg-border"
        >
          <div
            v-for="stat in [
              { label: 'Clips', value: formatNum(profile.clips.length) },
              { label: 'Total plays', value: formatNum(totalPlays) },
              { label: 'Total likes', value: formatNum(totalLikes) },
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

        <!-- ---- Tabs ---- -->
        <div class="mt-9">
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
          </div>

          <!-- Tab content -->
          <div class="mt-6">
            <!-- Clips tab -->
            <div v-if="tab === 'clips'">
              <div v-if="profile.clips.length === 0" class="flex items-center justify-center py-20">
                <p class="font-mono text-[13px] tracking-[0.06em] text-text-muted">No clips yet.</p>
              </div>
              <div v-else class="feed-grid">
                <ClipCard
                  v-for="clip in profile.clips"
                  :key="clip.id"
                  :clip="clip"
                  :show-author="false"
                  @click="router.push({ name: 'clip', params: { id: clip.id } })"
                />
              </div>
            </div>

            <!-- Liked tab — placeholder until /me/liked exists (Phase 3) -->
            <div v-else-if="tab === 'liked'" class="flex items-center justify-center py-20">
              <p class="font-mono text-[13px] tracking-[0.06em] text-text-muted">
                Liked clips are private.
              </p>
            </div>
          </div>
        </div>
      </div>
    </main>
  </div>
</template>
