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

// User-uploaded banner replaces the username-hashed gradient when present; otherwise the
// gradient remains as the fallback so a brand-new account still has a non-empty banner.
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

    <main v-else-if="profile" class="relative" :style="accentStyle">
      <!-- ===================== BANNER ===================== -->
      <div
        class="relative h-70 overflow-hidden"
        :style="bannerImageUrl ? undefined : { background: bannerGradient }"
      >
        <img
          v-if="bannerImageUrl"
          :src="bannerImageUrl"
          :alt="`${profile.username}'s banner`"
          class="absolute inset-0 h-full w-full object-cover"
        />
        <div
          v-if="!bannerImageUrl"
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
            class="flex h-35 w-35 shrink-0 select-none items-center justify-center rounded-full border-4 font-heading text-[56px] font-bold tracking-[-0.02em] text-white"
            :class="profile.accentColor ? '' : 'border-surface-base'"
            :style="{
              background: bannerGradient,
              ...(profile.accentColor ? { borderColor: profile.accentColor } : {}),
            }"
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

            <!-- Social links row. Rendered only when at least one handle exists; each opens
                 in a new tab with rel=noopener so the destination can't navigate this window. -->
            <div v-if="socialLinkEntries.length" class="mt-2.5 flex flex-wrap items-center gap-2">
              <a
                v-for="entry in socialLinkEntries"
                :key="entry.label"
                :href="entry.href"
                target="_blank"
                rel="noopener noreferrer"
                class="inline-flex items-center rounded-sm border border-border bg-surface-raised px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-secondary transition-colors duration-150 hover:border-border-hover hover:text-text-primary"
              >
                {{ entry.label }}
              </a>
            </div>
          </div>

          <!-- Action buttons (follow + share + more) -->
          <div class="flex flex-wrap items-center gap-2 pt-19">
            <button
              v-if="canShowFollowButton"
              :class="[
                'flex h-9 cursor-pointer items-center rounded-sm px-4 font-mono text-[11px] uppercase tracking-[0.08em] transition-all duration-150 disabled:opacity-60',
                profile.followedByMe
                  ? 'bg-brand text-white hover:bg-brand-light'
                  : 'border border-border bg-surface-raised text-text-primary hover:border-border-hover',
              ]"
              :disabled="followBusy"
              :aria-pressed="profile.followedByMe === true"
              @click="toggleFollow"
            >
              {{ profile.followedByMe ? 'Following' : 'Follow' }}
            </button>
            <button
              class="flex h-9 w-9 cursor-pointer items-center justify-center rounded-sm border border-border bg-surface-raised text-text-secondary transition-[border-color] duration-150 hover:border-border-hover"
              aria-label="Share profile"
              @click="copyShareUrl"
            >
              <IconShare :size="14" />
            </button>
            <button
              v-if="canShowFollowButton"
              class="flex h-9 cursor-pointer items-center rounded-sm border border-border bg-surface-raised px-3 font-mono text-[11px] uppercase tracking-[0.08em] text-text-secondary transition-colors duration-150 hover:border-[color:var(--color-error)] hover:text-[color:var(--color-error)]"
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
              class="font-mono text-[11px] uppercase tracking-widest text-neon"
            >
              {{ copyMessage }}
            </span>
          </div>
        </div>

        <!-- ---- Stat block ----
             Followers + Following render as RouterLinks when their count is > 0
             so a viewer can drill into the list. We keep a 0-count cell as a
             plain <div> so it doesn't look (or behave) clickable when the
             destination would just be an empty list. -->
        <div
          class="mt-7 grid grid-cols-[repeat(auto-fit,minmax(140px,1fr))] gap-px overflow-hidden rounded-md border border-border bg-border"
        >
          <div class="flex flex-col gap-1 bg-surface-raised px-5 py-4">
            <span class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >Clips</span
            >
            <span class="font-heading text-[22px] font-bold leading-none text-text-primary">{{
              formatNum(profile.clips.length)
            }}</span>
          </div>

          <component
            :is="profile.followerCount > 0 ? 'RouterLink' : 'div'"
            :to="
              profile.followerCount > 0
                ? { name: 'user-followers', params: { username: profile.username } }
                : undefined
            "
            :class="[
              'flex flex-col gap-1 bg-surface-raised px-5 py-4 no-underline',
              profile.followerCount > 0
                ? 'cursor-pointer transition-colors duration-150 hover:bg-surface-overlay'
                : '',
            ]"
          >
            <span class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >Followers</span
            >
            <span class="font-heading text-[22px] font-bold leading-none text-text-primary">{{
              formatNum(profile.followerCount)
            }}</span>
          </component>

          <component
            :is="profile.followingCount > 0 ? 'RouterLink' : 'div'"
            :to="
              profile.followingCount > 0
                ? { name: 'user-following', params: { username: profile.username } }
                : undefined
            "
            :class="[
              'flex flex-col gap-1 bg-surface-raised px-5 py-4 no-underline',
              profile.followingCount > 0
                ? 'cursor-pointer transition-colors duration-150 hover:bg-surface-overlay'
                : '',
            ]"
          >
            <span class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >Following</span
            >
            <span class="font-heading text-[22px] font-bold leading-none text-text-primary">{{
              formatNum(profile.followingCount)
            }}</span>
          </component>

          <div
            v-for="stat in [
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
          <UnderlineTabs :tabs="TABS" :active="tab" @select="(k) => (tab = k)" />

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
