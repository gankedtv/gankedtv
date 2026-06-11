<script setup lang="ts">
import { ref, computed, watch, onBeforeUnmount } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { users, type UserProfile } from '@/api/users'
import { follows } from '@/api/follows'
import { useAuthStore } from '@/stores/auth'
import { safeImageUrl } from '@/lib/url'
import { formatNum } from '@/lib/format'
import ClipCard from '@/components/ClipCard.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import TelemetryStrip, { type TelemetryCell } from '@/components/TelemetryStrip.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import IconShare from '@/components/icons/IconShare.vue'
import KebabMenu, { type KebabMenuItem } from '@/components/KebabMenu.vue'
import ReportDialog from '@/components/ReportDialog.vue'
import ProfileEditModal from '@/components/profile/ProfileEditModal.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const profile = ref<UserProfile | null>(null)
const loading = ref(false)
const errored = ref(false)
const followBusy = ref(false)

// Identity-based — `auth.user.id === profile.id` is more reliable than username string
// equality (case, future username changes). When the viewer isn't signed in, this is
// always false and the follow button shows for any profile.
const isMe = computed(() => !!auth.user && !!profile.value && auth.user.id === profile.value.id)
const canShowFollowButton = computed(() => auth.isAuthenticated && !!profile.value && !isMe.value)

const reportOpen = ref(false)

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
  // Reset the follow-button busy state alongside the profile so a stale toggle
  // (whose response gets dropped by the load-id fence below) can't leave the
  // button disabled on the freshly-loaded profile.
  followBusy.value = false
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
// Flat, low-chroma fill — gradients are banned outside thumbnail fallbacks.
const avatarColor = computed(() => {
  const name = profile.value?.username ?? ''
  let hash = 0
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0
  return `hsl(${Math.abs(hash) % 360}, 35%, 38%)`
})

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

// User-uploaded banner replaces the username-hashed fill when present; otherwise the
// flat fill remains as the fallback so a brand-new account still has a non-empty banner.
const bannerImageUrl = computed(() => safeImageUrl(profile.value?.bannerUrl))

// Inline style binding for the profile header — DESIGN.md mandates that user-picked colors
// flow via inline :style (CSS variables), not Tailwind classes, since each profile carries
// its own color and a class can't carry runtime values.
const accentStyle = computed<Record<string, string>>(() => {
  const c = profile.value?.accentColor
  const out: Record<string, string> = {}
  if (c) out['--profile-accent'] = c
  return out
})

const socialLinkEntries = computed(() => {
  const s = profile.value?.socialLinks
  if (!s) return []
  return [
    s.twitch
      ? { label: 'Twitch', href: `https://twitch.tv/${encodeURIComponent(s.twitch)}` }
      : null,
    s.youtube
      ? { label: 'YouTube', href: `https://youtube.com/@${encodeURIComponent(s.youtube)}` }
      : null,
    s.twitter ? { label: 'X', href: `https://x.com/${encodeURIComponent(s.twitter)}` } : null,
  ].filter((x): x is { label: string; href: string } => x !== null)
})

const editOpen = ref(false)

async function onProfileSaved() {
  // The modal already updated the auth.user via fetchMe; re-fetch the public profile so
  // the page renders the new banner/accent/socials without a manual refresh.
  if (username.value) await loadProfile(username.value)
}

const joinedDate = computed(() => {
  if (!profile.value) return ''
  return new Date(profile.value.createdAt).toLocaleString(undefined, {
    month: 'short',
    year: 'numeric',
  })
})

const totalPlays = computed(() => (profile.value?.clips ?? []).reduce((s, c) => s + c.viewCount, 0))
const totalLikes = computed(() => (profile.value?.clips ?? []).reduce((s, c) => s + c.likeCount, 0))

// Hybrid-voice telemetry strip. Followers/Following become tappable cells when
// their count is > 0 so a viewer can drill into the list; a 0-count cell stays
// inert so it doesn't look clickable when the destination would be empty.
const telemetryCells = computed<TelemetryCell[]>(() => {
  const p = profile.value
  if (!p) return []
  return [
    { key: 'clips', label: 'Clips', value: formatNum(p.clips.length) },
    {
      key: 'followers',
      label: 'Followers',
      value: formatNum(p.followerCount),
      ink: true,
      action: p.followerCount > 0,
    },
    {
      key: 'following',
      label: 'Following',
      value: formatNum(p.followingCount),
      action: p.followingCount > 0,
    },
    { key: 'plays', label: 'Total plays', value: formatNum(totalPlays.value) },
    { key: 'likes', label: 'Total likes', value: formatNum(totalLikes.value) },
  ]
})

function onTelemetryClick(key: string) {
  const p = profile.value
  if (!p) return
  if (key === 'followers') {
    router.push({ name: 'user-followers', params: { username: p.username } })
  } else if (key === 'following') {
    router.push({ name: 'user-following', params: { username: p.username } })
  }
}

async function toggleFollow() {
  if (!profile.value || followBusy.value) return
  // Mirrors the like button: redirect to login when unauthenticated so the user can
  // come back and try again rather than silently failing.
  if (!auth.isAuthenticated) {
    router.push({ name: 'login', query: { redirect: route.fullPath } })
    return
  }

  const targetUsername = profile.value.username
  // Capture the profile-load generation so A→B→A navigation (same username,
  // different profile object) can't apply this toggle's response to a freshly
  // loaded profile. A username-only check would falsely accept it.
  const requestLoadId = latestLoadId
  const wasFollowing = profile.value.followedByMe === true
  // Optimistic flip — same pattern as ClipView's like toggle. The +/- 1 can drift
  // out of sync with reality if the same user toggles follow state in another tab
  // between page load and this click; a hard refresh resolves it. Multi-tab
  // consistency isn't required for v1 and follow ops are idempotent on the server,
  // so the worst case is a stale counter, not a state corruption.
  profile.value.followedByMe = !wasFollowing
  profile.value.followerCount += wasFollowing ? -1 : 1
  followBusy.value = true
  try {
    if (wasFollowing) {
      await follows.unfollow(targetUsername)
    } else {
      await follows.follow(targetUsername)
    }
    if (latestLoadId !== requestLoadId) return
  } catch {
    if (latestLoadId !== requestLoadId) return
    // Roll back.
    profile.value.followedByMe = wasFollowing
    profile.value.followerCount += wasFollowing ? 1 : -1
  } finally {
    // Only clear busy when this invocation is still the latest. If the profile
    // has been reloaded since, loadProfile already cleared followBusy and the
    // new profile may have its own toggle in flight — don't stomp it.
    if (latestLoadId === requestLoadId) followBusy.value = false
  }
}

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

// Own-profile kebab — Edit profile + Sign out. Reusable `KebabMenu` handles open/close,
// outside-click, and Esc; this view only declares the items.
const ownProfileMenuItems = computed<KebabMenuItem[]>(() => [
  { label: 'Edit profile', onClick: () => (editOpen.value = true) },
  { label: 'Sign out', variant: 'danger', onClick: () => auth.logout() },
])

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
         BEFORE the root element makes the component multi-root (comment + div), which
         <Transition> can't drive — its leave never resolves, so the next route's view
         never mounts (issue #92). A comment (or a v-if chain with no bare v-else falling
         through to a comment) INSIDE the root is harmless — only nodes at the component
         root matter. -->
    <main v-if="loading">
      <StatusPanel kind="loading" message="Loading" />
    </main>

    <main v-else-if="errored">
      <StatusPanel kind="error" message="Couldn't load this profile.">
        <button
          class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
          @click="username && loadProfile(username)"
        >
          Retry
        </button>
      </StatusPanel>
    </main>

    <main v-else-if="profile" class="relative" :style="accentStyle">
      <!-- ===================== BANNER ===================== -->
      <!-- User-uploaded banner when present; otherwise a flat username-hashed
           fill. Hairline bottom rule instead of a fade — borders define regions. -->
      <div
        v-if="bannerImageUrl || !profile.accentColor"
        data-testid="banner"
        class="relative h-44 overflow-hidden border-b border-border"
        :style="bannerImageUrl ? undefined : { background: avatarColor }"
      >
        <img
          v-if="bannerImageUrl"
          :src="bannerImageUrl"
          :alt="`${profile.username}'s banner`"
          class="absolute inset-0 h-full w-full object-cover"
        />
      </div>
      <div
        v-else
        data-testid="banner"
        class="relative h-44 overflow-hidden border-b border-border"
        :style="{ background: profile.accentColor }"
      ></div>

      <!-- ===================== INNER CONTENT ===================== -->
      <div class="mx-auto max-w-7xl px-6 pb-30 max-tablet:px-4">
        <!-- Breadcrumb -->
        <div class="pt-5">
          <button
            class="flex cursor-pointer items-center gap-1.5 border-none bg-transparent p-0 font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted transition-colors duration-150 hover:text-ink"
            @click="router.push({ name: 'home' })"
          >
            ← Feed / @{{ profile.username }}
          </button>
        </div>

        <!-- ---- Editorial header ---- -->
        <div class="mt-5 flex flex-wrap items-start gap-7">
          <!-- Large square avatar -->
          <div
            class="flex size-22 shrink-0 select-none items-center justify-center overflow-hidden border border-border font-heading text-4xl font-bold tracking-[-0.02em] text-white"
            :style="avatarImageUrl ? undefined : { background: avatarColor }"
          >
            <img
              v-if="avatarImageUrl"
              :src="avatarImageUrl"
              :alt="profile.username"
              class="h-full w-full object-cover"
            />
            <span v-else>{{ initials }}</span>
          </div>

          <!-- User info -->
          <div class="min-w-55 flex-1">
            <div
              class="mb-1.5 font-mono text-[10px] uppercase tracking-[0.22em] text-text-secondary"
            >
              <span class="text-ink">Creator</span> · Filed since {{ joinedDate }}
            </div>

            <h1
              class="m-0 font-heading text-[clamp(36px,4.5vw,52px)] font-bold leading-none uppercase tracking-[0.01em] text-text-primary"
            >
              {{ profile.username }}
            </h1>

            <div class="mt-1.5 font-mono text-sm tracking-[0.04em] text-ink">
              @{{ profile.username }}
            </div>

            <p
              v-if="profile.bio"
              class="m-0 mt-2.5 max-w-130 text-sm leading-[1.55] text-text-secondary"
            >
              {{ profile.bio }}
            </p>

            <!-- Social links row. Rendered only when at least one handle exists; each opens
                 in a new tab with rel=noopener so the destination can't navigate this window. -->
            <div v-if="socialLinkEntries.length" class="mt-2.5 flex flex-wrap items-center gap-2">
              <a
                v-for="entry in socialLinkEntries"
                :key="entry.label"
                :href="entry.href"
                target="_blank"
                rel="noopener noreferrer"
                class="inline-flex items-center border border-border px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink"
              >
                {{ entry.label }}
              </a>
            </div>
          </div>

          <!-- Action buttons (follow + share + more) -->
          <div class="flex flex-wrap items-center gap-2">
            <button
              v-if="canShowFollowButton"
              :class="[
                'flex h-9 cursor-pointer items-center px-4 font-mono text-[11px] uppercase tracking-[0.08em] transition-colors duration-150 disabled:opacity-60',
                profile.followedByMe
                  ? 'border border-border bg-transparent text-text-primary hover:border-ink hover:text-ink'
                  : 'bg-ink text-signal-text transition-[filter] hover:brightness-108',
              ]"
              :disabled="followBusy"
              :aria-pressed="profile.followedByMe === true"
              @click="toggleFollow"
            >
              {{ profile.followedByMe ? 'Following' : 'Follow' }}
            </button>
            <button
              class="flex h-9 w-9 cursor-pointer items-center justify-center border border-border bg-transparent text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink"
              aria-label="Share profile"
              @click="copyShareUrl"
            >
              <IconShare :size="14" />
            </button>
            <button
              v-if="canShowFollowButton"
              class="flex h-9 cursor-pointer items-center border border-border bg-transparent px-3 font-mono text-[11px] uppercase tracking-[0.08em] text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink"
              aria-label="Report user"
              @click="reportOpen = true"
            >
              Report
            </button>
            <!-- Kebab menu (own profile only): houses Sign out. Foreign profiles get the
                 Report button above instead, so the kebab would have no contents — we hide
                 it rather than render an empty/placeholder button. -->
            <KebabMenu
              v-if="isMe"
              :items="ownProfileMenuItems"
              icon-orientation="horizontal"
              trigger-variant="outlined"
            />
            <span
              v-if="copyMessage"
              aria-live="polite"
              class="font-mono text-[11px] uppercase tracking-widest text-ink"
            >
              {{ copyMessage }}
            </span>
          </div>
        </div>

        <!-- ---- Telemetry strip ---- -->
        <TelemetryStrip class="mt-7" :cells="telemetryCells" @cell-click="onTelemetryClick" />

        <!-- ---- Tabs ---- -->
        <div class="mt-9">
          <UnderlineTabs :tabs="TABS" :active="tab" @select="(k) => (tab = k)" />

          <!-- Tab content -->
          <div class="mt-6">
            <!-- Clips tab -->
            <div v-if="tab === 'clips'">
              <div v-if="profile.clips.length === 0" class="flex items-center justify-center py-20">
                <p class="font-mono text-[13px] tracking-[0.06em] text-text-muted">No clips yet.</p>
              </div>
              <div
                v-else
                data-testid="clips-grid"
                class="grid grid-cols-3 gap-x-5.5 gap-y-7 max-lg:grid-cols-2 max-tablet:grid-cols-1"
              >
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

    <ReportDialog
      v-if="profile"
      :open="reportOpen"
      target-type="user"
      :target-id="profile.id"
      @cancel="reportOpen = false"
      @submitted="reportOpen = false"
    />

    <ProfileEditModal
      v-if="isMe"
      :open="editOpen"
      @close="editOpen = false"
      @saved="onProfileSaved"
    />
  </div>
</template>
