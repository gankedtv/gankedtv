<script setup lang="ts">
import { ref, computed, watch, onBeforeUnmount, useTemplateRef } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Plyr from 'plyr'
import 'plyr/dist/plyr.css'
import { ApiError } from '@/api/client'
import { clips, type ClipDetail } from '@/api/clips'
import { formatNum, formatRelativeTime } from '@/lib/format'
import { useAuthStore } from '@/stores/auth'
import { safeImageUrl } from '@/lib/url'
import GameTag from '@/components/GameTag.vue'
import TagChip from '@/components/TagChip.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import ClipEditDialog from '@/components/ClipEditDialog.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import CommentsSection from '@/components/CommentsSection.vue'
import IconHeart from '@/components/icons/IconHeart.vue'
import IconShare from '@/components/icons/IconShare.vue'
import IconMoreVertical from '@/components/icons/IconMoreVertical.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const clip = ref<ClipDetail | null>(null)
const loading = ref(false)
const errored = ref(false)
const liked = ref(false)
const likeCount = ref(0)
const likeBusy = ref(false)
const showToast = ref(false)
const toastText = ref('')

const videoEl = useTemplateRef<HTMLVideoElement>('videoEl')
let player: Plyr | null = null

// View tracking: fire POST /clips/{id}/view exactly once per mount after ~3s of
// accumulated playback. Bounded per-tick delta caps seeking jumps (current_time can
// jump forwards on a scrub) so a single seek doesn't trigger an instant record.
// `viewRecordedForClipId` is intentionally never cleared: re-navigating to the same
// clip within the SPA session won't re-ping. The server-side 30-min dedup would
// collapse it anyway, and erring on under-count beats over-counting on remount.
let viewRecordedForClipId: string | null = null
let playedMs = 0
let lastTickTime = 0
let viewTickListener: { el: HTMLVideoElement; handler: () => void } | null = null

function detachViewTracking() {
  if (viewTickListener) {
    viewTickListener.el.removeEventListener('timeupdate', viewTickListener.handler)
    viewTickListener = null
  }
}

// Monotonic request counter — guards against A→B→A races where comparing
// `clipId.value === id` would falsely accept the first A response after the
// second A request supersedes it.
let latestLoadId = 0

const clipId = computed(() => {
  const id = route.params.id
  return Array.isArray(id) ? id[0] : (id as string | undefined)
})
const shareCode = computed(() => {
  const code = route.params.code
  return Array.isArray(code) ? code[0] : (code as string | undefined)
})

async function loadClip() {
  const myLoadId = ++latestLoadId
  loading.value = true
  errored.value = false
  clip.value = null
  teardownPlayer()
  try {
    const fetched = shareCode.value
      ? await clips.getByShareCode(shareCode.value)
      : await clips.getDetail(clipId.value!)
    if (myLoadId !== latestLoadId) return
    clip.value = fetched
    liked.value = fetched.likedByMe
    likeCount.value = fetched.likeCount
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
  [clipId, shareCode],
  ([id, code]) => {
    if (!id && !code) return
    loadClip()
  },
  { immediate: true },
)

// Mount Plyr on the <video> element after both the element and the clip data exist.
// We watch both — Vue may render the <video> before the API resolves, or vice versa.
watch(
  [clip, videoEl],
  ([detail, el]) => {
    if (!detail || !el || player) return
    el.src = detail.videoUrl
    player = new Plyr(el, {
      controls: ['play-large', 'play', 'progress', 'current-time', 'mute', 'volume', 'fullscreen'],
      tooltips: { controls: true, seek: true },
    })
    attachViewTracking(detail.id, el)
  },
  { flush: 'post' },
)

// Bind to the underlying <video> element's `timeupdate` (not Plyr's wrapper) — Plyr
// re-fires the same DOM event but the element listener stays valid across Plyr lifecycle
// quirks. Per-tick delta is clamped to [0, 1000ms] so a scrub forward doesn't credit the
// gap, and a scrub backward doesn't subtract.
function attachViewTracking(targetClipId: string, el: HTMLVideoElement) {
  detachViewTracking()
  playedMs = 0
  lastTickTime = el.currentTime * 1000
  const onTick = () => {
    if (viewRecordedForClipId === targetClipId || el.paused) {
      lastTickTime = el.currentTime * 1000
      return
    }
    const now = el.currentTime * 1000
    const delta = now - lastTickTime
    lastTickTime = now
    if (delta > 0 && delta < 1000) {
      playedMs += delta
    }
    if (playedMs >= 3000) {
      viewRecordedForClipId = targetClipId
      void clips.recordView(targetClipId).catch(() => {
        // Silent: view tracking is best-effort. A failed ping shouldn't surface to the user
        // and shouldn't retry — the server's rate limit + dedup means retries hurt more than help.
      })
    }
  }
  el.addEventListener('timeupdate', onTick)
  viewTickListener = { el, handler: onTick }
}

function teardownPlayer() {
  detachViewTracking()
  if (player) {
    player.destroy()
    player = null
  }
}

let toastTimer: ReturnType<typeof setTimeout> | null = null
function fireToast(text: string) {
  toastText.value = text
  showToast.value = true
  if (toastTimer !== null) clearTimeout(toastTimer)
  toastTimer = setTimeout(() => {
    showToast.value = false
  }, 2400)
}

onBeforeUnmount(() => {
  teardownPlayer()
  if (toastTimer !== null) clearTimeout(toastTimer)
  window.removeEventListener('keydown', onMenuKeydown)
  window.removeEventListener('click', onMenuClickOutside, true)
})

async function toggleLike() {
  if (!clip.value || likeBusy.value) return
  if (!auth.isAuthenticated) {
    router.push({ name: 'login', query: { redirect: route.fullPath } })
    return
  }
  // Optimistic UI: flip locally first, roll back on error so a flaky network doesn't strand
  // the user on a wrong-looking counter.
  const targetId = clip.value.id
  const wasLiked = liked.value
  liked.value = !wasLiked
  likeCount.value += wasLiked ? -1 : 1
  likeBusy.value = true
  try {
    const res = wasLiked ? await clips.unlike(targetId) : await clips.like(targetId)
    // If the user navigated to a different clip while the request was in flight,
    // skip the apply so we don't stamp this clip's count onto the next one.
    if (clip.value?.id !== targetId) return
    liked.value = res.liked
    likeCount.value = res.likeCount
    if (res.liked) fireToast('♥ Added to your liked clips')
  } catch {
    if (clip.value?.id !== targetId) return
    liked.value = wasLiked
    likeCount.value += wasLiked ? 1 : -1
    fireToast('Could not update like — try again')
  } finally {
    likeBusy.value = false
  }
}

async function handleShare() {
  try {
    const url = clip.value?.shareCode
      ? `${window.location.origin}/c/${clip.value.shareCode}`
      : window.location.href
    await navigator.clipboard.writeText(url)
    fireToast('🔗 Link copied to clipboard')
  } catch {
    fireToast('Copy failed')
  }
}

const initialsFor = (username: string): string =>
  username
    .replace(/[^a-zA-Z]/g, '')
    .slice(0, 2)
    .toUpperCase() || '??'

const authorColor = computed(() => {
  const name = clip.value?.author.username ?? ''
  let hash = 0
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0
  return `hsl(${Math.abs(hash) % 360}, 65%, 45%)`
})

// Hoisted so the template doesn't re-parse the URL on every render.
const authorAvatarUrl = computed(() => safeImageUrl(clip.value?.author.avatarUrl))

const menuOpen = ref(false)
const editOpen = ref(false)
const deleteOpen = ref(false)
const deleting = ref(false)

const isOwner = computed(() => !!auth.user && !!clip.value && clip.value.author.id === auth.user.id)

function openMenu() {
  menuOpen.value = true
}

function closeMenu() {
  menuOpen.value = false
}

function onMenuKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') closeMenu()
}

function onMenuClickOutside(e: MouseEvent) {
  const target = e.target as Node | null
  const menuEl = document.getElementById('clip-more-menu')
  if (menuEl && !menuEl.contains(target)) closeMenu()
}

watch(menuOpen, (open) => {
  if (open) {
    window.addEventListener('keydown', onMenuKeydown)
    window.addEventListener('click', onMenuClickOutside, true)
  } else {
    window.removeEventListener('keydown', onMenuKeydown)
    window.removeEventListener('click', onMenuClickOutside, true)
  }
})

function onSaved(updated: ClipDetail) {
  clip.value = updated
  fireToast('Clip updated')
}

function onEditError(message: string) {
  fireToast(message)
}

function openEdit() {
  closeMenu()
  editOpen.value = true
}

function openDelete() {
  closeMenu()
  deleteOpen.value = true
}

const DELETE_ERROR_CODES: Record<string, string> = {
  forbidden: "You don't have permission to delete this clip",
  not_found: 'Clip not found',
  unauthorized: 'You need to be logged in to delete this clip',
}

async function onConfirmDelete() {
  if (!clip.value || !auth.user) return
  deleting.value = true
  try {
    await clips.delete(clip.value.id)
    fireToast('Clip deleted')
    await router.push({ name: 'user', params: { username: auth.user.username } })
  } catch (err) {
    let message = 'Failed to delete clip'
    if (err instanceof ApiError) {
      const code = (err.body as { code?: string } | null)?.code
      if (code && DELETE_ERROR_CODES[code]) message = DELETE_ERROR_CODES[code]
    }
    fireToast(message)
  } finally {
    deleting.value = false
    deleteOpen.value = false
  }
}
</script>

<template>
  <div class="mx-auto max-w-350 px-6 pt-8 pb-30">
    <!-- Loading -->
    <StatusPanel v-if="loading" kind="loading" message="Loading…" />

    <!-- Error -->
    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load this clip.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-raised px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="(clipId || shareCode) && loadClip()"
      >
        Retry
      </button>
    </StatusPanel>

    <div v-else-if="clip">
      <!-- Breadcrumb -->
      <div
        class="mb-5 flex items-center gap-2 font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted"
      >
        <router-link to="/" class="transition-colors hover:text-text-secondary">Feed</router-link>
        <span>/</span>
        <span>{{ clip.id.slice(0, 8) }}</span>
      </div>

      <!-- Video Player -->
      <div class="overflow-hidden rounded-md border border-border bg-black">
        <video ref="videoEl" controls playsinline class="block aspect-video w-full"></video>
      </div>

      <!-- Title + Meta Row -->
      <div class="mt-5">
        <div v-if="clip.game" class="mb-2">
          <GameTag :tag="clip.game.tag" />
          <span class="ml-2 font-mono text-[11px] uppercase tracking-[0.06em] text-text-muted">
            {{ clip.game.name }}
          </span>
        </div>
        <h1
          class="font-heading text-[34px] font-bold leading-[1.05] uppercase tracking-[0.01em] text-text-primary"
        >
          {{ clip.title }}
        </h1>

        <div v-if="clip.tags.length" class="mt-3 flex flex-wrap gap-2">
          <TagChip v-for="t in clip.tags" :key="t.id" :slug="t.slug" :name="t.name" size="md" />
        </div>

        <div class="mt-4 flex flex-wrap items-center gap-3">
          <!-- Author info -->
          <div class="flex items-center gap-2">
            <span
              class="inline-flex h-9 w-9 shrink-0 items-center justify-center overflow-hidden rounded-full font-mono text-xs font-semibold text-white"
              :style="{
                background: `linear-gradient(135deg, ${authorColor}, color-mix(in oklab, ${authorColor} 40%, #000))`,
              }"
            >
              <img
                v-if="authorAvatarUrl"
                :src="authorAvatarUrl"
                :alt="clip.author.username"
                class="h-full w-full object-cover"
              />
              <span v-else>{{ initialsFor(clip.author.username) }}</span>
            </span>
            <div>
              <AuthorHandle
                :username="clip.author.username"
                as="link"
                class="text-[13px] font-semibold text-neon"
              />
              <div class="font-mono text-[10px] tracking-[0.04em] text-text-muted">
                Uploaded {{ formatRelativeTime(clip.createdAt) }} ago
              </div>
            </div>
          </div>

          <div class="flex-1" />

          <!-- Action buttons -->
          <div class="flex items-center gap-2">
            <button
              class="flex items-center gap-1.5 rounded px-3 py-1.5 font-mono text-[12px] transition-all duration-150 disabled:opacity-60"
              :class="
                liked
                  ? 'bg-brand text-white'
                  : 'border border-border bg-surface-raised text-text-secondary'
              "
              :disabled="likeBusy"
              @click="toggleLike"
            >
              <IconHeart :size="14" />
              <span>{{ formatNum(likeCount) }}</span>
            </button>

            <button
              class="flex items-center gap-1.5 rounded border border-border bg-surface-raised px-3 py-1.5 font-mono text-[12px] text-text-secondary transition-all duration-150"
              @click="handleShare"
            >
              <IconShare :size="14" />
              <span>Share</span>
            </button>

            <div v-if="isOwner" id="clip-more-menu" class="relative">
              <button
                class="flex h-7 w-7 items-center justify-center rounded text-text-secondary transition-colors hover:bg-surface-raised"
                aria-label="More options"
                aria-haspopup="true"
                :aria-expanded="menuOpen"
                @click.stop="menuOpen ? closeMenu() : openMenu()"
              >
                <IconMoreVertical :size="16" />
              </button>
              <div
                v-if="menuOpen"
                class="absolute right-0 top-full z-20 mt-1 min-w-32 rounded-md border border-border-strong bg-surface-overlay shadow-[0_4px_20px_rgba(0,0,0,0.4)]"
              >
                <button
                  type="button"
                  class="w-full cursor-pointer rounded-md px-4 py-2.5 text-left font-body text-sm text-text-primary transition-colors duration-100 hover:bg-surface-raised"
                  @click="openEdit"
                >
                  Edit
                </button>
                <button
                  type="button"
                  class="w-full cursor-pointer rounded-md px-4 py-2.5 text-left font-body text-sm text-error transition-colors duration-100 hover:bg-surface-raised"
                  @click="openDelete"
                >
                  Delete
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- Stat Block -->
      <div class="mt-6 grid grid-cols-3 gap-px overflow-hidden rounded-md bg-border">
        <div
          v-for="stat in [
            { label: 'Plays', value: formatNum(clip.viewCount) },
            { label: 'Likes', value: formatNum(likeCount) },
            { label: 'Uploaded', value: formatRelativeTime(clip.createdAt) + ' ago' },
          ]"
          :key="stat.label"
          class="flex flex-col gap-1 bg-surface-raised px-4 py-3"
        >
          <span class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">{{
            stat.label
          }}</span>
          <span class="font-heading text-xl font-bold leading-[1.2] text-text-primary">{{
            stat.value
          }}</span>
        </div>
      </div>

      <!-- Description -->
      <div
        v-if="clip.description"
        class="mt-4 rounded-md border border-border bg-surface-raised p-4"
      >
        <div class="mb-2 font-mono text-[10px] uppercase tracking-widest text-text-muted">
          Description
        </div>
        <p class="text-sm leading-[1.6] text-text-secondary">{{ clip.description }}</p>
      </div>

      <!-- Comments -->
      <CommentsSection :clip-id="clip.id" />
    </div>

    <ClipEditDialog
      v-if="clip"
      :clip="clip"
      :open="editOpen"
      @close="editOpen = false"
      @saved="onSaved"
      @error="onEditError"
    />

    <ConfirmDialog
      :open="deleteOpen"
      title="Delete clip?"
      body="This permanently removes the clip and its video file. This can't be undone."
      confirm-label="Delete"
      variant="danger"
      :busy="deleting"
      @cancel="deleteOpen = false"
      @confirm="onConfirmDelete"
    />

    <!-- Toast — kept inside the page's single root so the route-level
         <Transition mode="out-in"> can animate the leave cleanly. The toast itself
         is position:fixed, so DOM nesting doesn't affect where it renders. -->
    <Transition
      enter-active-class="animate-[slideUp_0.22s_ease-out_forwards]"
      leave-active-class="animate-[slideDown_0.2s_ease-in_forwards]"
    >
      <div
        v-if="showToast"
        class="fixed bottom-6 left-1/2 z-9999 flex -translate-x-1/2 items-center gap-2 rounded-md border border-brand bg-surface-overlay px-4 py-3 font-mono text-[13px] tracking-[0.04em] whitespace-nowrap text-text-primary shadow-[0_0_20px_var(--color-brand-glow)]"
      >
        {{ toastText }}
      </div>
    </Transition>
  </div>
</template>
