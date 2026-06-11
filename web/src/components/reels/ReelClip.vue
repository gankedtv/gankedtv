<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { clips, type ClipDetail, type ClipFeedItem } from '@/api/clips'
import { useAuthStore } from '@/stores/auth'
import { formatNum } from '@/lib/format'
import UserAvatar from '@/components/UserAvatar.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import CommentsSection from '@/components/CommentsSection.vue'
import IconHeart from '@/components/icons/IconHeart.vue'
import IconVolume from '@/components/icons/IconVolume.vue'
import IconVolumeMute from '@/components/icons/IconVolumeMute.vue'
import IconMessageCircle from '@/components/icons/IconMessageCircle.vue'
import IconX from '@/components/icons/IconX.vue'
import IconPlay from '@/components/icons/IconPlay.vue'

const props = defineProps<{
  clip: ClipFeedItem
  detail: ClipDetail | null
  detailErrored: boolean
  isActive: boolean
  globalMuted: boolean
}>()

const emit = defineEmits<{
  (e: 'toggle-mute'): void
  (e: 'retry-detail', id: string): void
  (e: 'liked-changed', payload: { id: string; liked: boolean; count: number }): void
  (e: 'view-recorded', id: string): void
}>()

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const videoEl = useTemplateRef<HTMLVideoElement>('videoEl')
const needsTapToPlay = ref(false)
const spinnerVisible = ref(false)
const commentsOpen = ref(false)
const codecUnsupported = ref(false)
let spinnerTimer: ReturnType<typeof setTimeout> | null = null

// AV1 capability probe — same MIME string ClipView uses. ClipView falls back to a
// just-in-time HLS stream; for reels we send the user to the detail page (which
// already orchestrates the JIT polling + hls.js attach). Keeping reels lean —
// no hls.js pulled into this slot, and one URL/codec decision per detail load.
const AV1_MIME = 'video/mp4; codecs="av01.0.05M.08"'

function canPlayCodec(codec: string | null, el: HTMLVideoElement | null): boolean {
  if (!el) return true
  if (!codec || codec === 'h264') return true
  if (codec === 'av1') return el.canPlayType(AV1_MIME) !== ''
  // Unknown codec — optimistically attempt direct playback rather than forcing
  // the user to bounce to the detail page on a maybe-playable file.
  return true
}

// Like state is local for snappy optimistic UI. Parent gets notified via emit so
// its `items[]` cache reflects the latest count if the user opens the detail page.
const liked = ref(props.clip.likedByMe)
const likeCount = ref(props.clip.likeCount)
const likeBusy = ref(false)

// Re-sync local like state when the clip prop swaps (parent may have updated
// its cache from elsewhere — e.g., a refetch).
watch(
  () => props.clip.id,
  () => {
    liked.value = props.clip.likedByMe
    likeCount.value = props.clip.likeCount
  },
)

// --- View tracking — direct port of ClipView's pattern -----------------------
// Per-instance accumulator. If the slot remounts later (e.g., user scrolls back
// to it after we trimmed the DOM), this re-arms — the server's 30-min dedup
// absorbs the revisit and we err on under-count.
let viewRecorded = false
let playedMs = 0
let lastTickMs = 0
let viewListener: { el: HTMLVideoElement; handler: () => void } | null = null

function attachViewTracking(targetClipId: string, el: HTMLVideoElement) {
  detachViewTracking()
  playedMs = 0
  lastTickMs = el.currentTime * 1000
  const onTick = () => {
    if (viewRecorded || el.paused) {
      lastTickMs = el.currentTime * 1000
      return
    }
    const now = el.currentTime * 1000
    const delta = now - lastTickMs
    lastTickMs = now
    // Clamp [0, 1000ms] so a scrub forward doesn't credit the gap and a scrub
    // back doesn't subtract.
    if (delta > 0 && delta < 1000) playedMs += delta
    if (playedMs >= 3000) {
      viewRecorded = true
      void clips.recordView(targetClipId).catch(() => {
        // Silent: best-effort. Retry would only fight the server's rate limit.
      })
      emit('view-recorded', targetClipId)
    }
  }
  el.addEventListener('timeupdate', onTick)
  viewListener = { el, handler: onTick }
}

function detachViewTracking() {
  if (viewListener) {
    viewListener.el.removeEventListener('timeupdate', viewListener.handler)
    viewListener = null
  }
}

// --- Playback lifecycle -------------------------------------------------------

watch(
  [() => props.isActive, () => props.detail, videoEl],
  ([active, detail, el]) => {
    if (!detail || !el) return
    codecUnsupported.value = !canPlayCodec(detail.videoCodec, el)
    if (codecUnsupported.value) {
      // Bail before play() — calling it on a clip the browser can't decode
      // just produces a silent black slot. The template renders an "Open in
      // detail" affordance so the user can fall through to the JIT-capable
      // player.
      el.pause()
      detachViewTracking()
      return
    }
    if (active) {
      el.muted = props.globalMuted
      el.currentTime = 0
      needsTapToPlay.value = false
      // jsdom returns undefined from play(); real browsers return a Promise.
      // Normalise so .catch is always safe and tests can run without a media
      // shim. Autoplay rejection (rare with muted=true) flips to tap-to-play.
      Promise.resolve(el.play()).catch(() => {
        needsTapToPlay.value = true
      })
      attachViewTracking(detail.id, el)
    } else {
      el.pause()
      detachViewTracking()
      playedMs = 0
      viewRecorded = false
    }
  },
  { flush: 'post' },
)

watch(
  () => props.globalMuted,
  (m) => {
    if (videoEl.value) videoEl.value.muted = m
  },
)

// Auto-close the comments sheet when the user scrolls to a different clip.
// Without this, the sheet would remain visible on the previously-active reel
// when scrolling, even though its parent slot is no longer the focus.
watch(
  () => props.isActive,
  (active) => {
    if (!active) commentsOpen.value = false
  },
)

// Spinner appears only after 250ms of waiting — avoids a flash on fast networks
// where detail resolves in <100ms.
watch(
  [() => props.detail, () => props.detailErrored],
  ([detail, errored]) => {
    if (spinnerTimer !== null) {
      clearTimeout(spinnerTimer)
      spinnerTimer = null
    }
    if (detail || errored) {
      spinnerVisible.value = false
      return
    }
    spinnerTimer = setTimeout(() => {
      spinnerVisible.value = true
    }, 250)
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  detachViewTracking()
  if (spinnerTimer !== null) clearTimeout(spinnerTimer)
})

// --- Interactions -------------------------------------------------------------

function handleTapToPlay() {
  if (!videoEl.value) return
  Promise.resolve(videoEl.value.play())
    .then(() => {
      needsTapToPlay.value = false
    })
    .catch(() => {
      // User gesture didn't help — leave the overlay visible.
    })
}

async function toggleLike() {
  if (likeBusy.value) return
  if (!auth.isAuthenticated) {
    router.push({
      name: 'login',
      query: { redirect: route.fullPath },
    })
    return
  }
  const targetId = props.clip.id
  const wasLiked = liked.value
  liked.value = !wasLiked
  likeCount.value += wasLiked ? -1 : 1
  likeBusy.value = true
  try {
    const res = wasLiked ? await clips.unlike(targetId) : await clips.like(targetId)
    if (props.clip.id !== targetId) return
    liked.value = res.liked
    likeCount.value = res.likeCount
    emit('liked-changed', { id: targetId, liked: res.liked, count: res.likeCount })
  } catch {
    if (props.clip.id !== targetId) return
    liked.value = wasLiked
    likeCount.value += wasLiked ? 1 : -1
  } finally {
    likeBusy.value = false
  }
}

function onToggleMute() {
  emit('toggle-mute')
}

function onRetryDetail() {
  emit('retry-detail', props.clip.id)
}

const detailHref = computed(() => `/clip/${props.clip.id}`)
const authorHref = computed(() => `/user/${props.clip.author.username}`)

function openComments() {
  commentsOpen.value = true
}

function closeComments() {
  commentsOpen.value = false
}

function onCommentsKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') closeComments()
}

watch(commentsOpen, (open) => {
  if (typeof window === 'undefined') return
  if (open) window.addEventListener('keydown', onCommentsKeydown)
  else window.removeEventListener('keydown', onCommentsKeydown)
})

onBeforeUnmount(() => {
  if (typeof window !== 'undefined') {
    window.removeEventListener('keydown', onCommentsKeydown)
  }
})
</script>

<template>
  <article
    class="relative flex h-full w-full items-center justify-center overflow-hidden bg-surface-sunken"
  >
    <!-- Thumbnail layer — visible until video element mounts. -->
    <img
      v-if="!detail"
      :src="clip.thumbnailUrl"
      :alt="clip.title"
      class="block max-h-full max-w-full object-contain"
    />

    <!-- Video layer. -->
    <video
      v-if="detail"
      ref="videoEl"
      :src="detail.videoUrl"
      :poster="clip.thumbnailUrl"
      :loop="true"
      :muted="globalMuted"
      playsinline
      preload="metadata"
      class="block max-h-full max-w-full object-contain"
    />

    <!-- Delayed loading ticker. -->
    <div
      v-if="spinnerVisible && !detail && !detailErrored"
      class="pointer-events-none absolute inset-0 flex items-center justify-center"
    >
      <span class="block h-1.5 w-5.5 overflow-hidden bg-white/15">
        <span
          class="block h-full w-full origin-left bg-ink animate-[tick_1.6s_ease-in-out_infinite]"
        ></span>
      </span>
    </div>

    <!-- Per-slot detail load error. -->
    <div
      v-if="detailErrored"
      class="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-black/50 text-center"
    >
      <p class="font-mono text-xs uppercase tracking-widest text-text-secondary">
        Couldn't load video
      </p>
      <button
        type="button"
        class="cursor-pointer border border-[#f4f1e8]/35 bg-black/45 px-4 py-2 font-mono text-xs uppercase tracking-widest text-[#f4f1e8] transition-colors duration-150 hover:border-ink hover:text-ink"
        @click="onRetryDetail"
      >
        Retry
      </button>
    </div>

    <!-- Codec the browser can't decode directly (e.g. AV1 on Safari). The detail
         page handles the just-in-time HLS fallback; reels just delegates rather
         than pulling hls.js + JIT polling into every slot. -->
    <div
      v-else-if="detail && codecUnsupported"
      class="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-black/50 px-6 text-center"
    >
      <p class="font-mono text-xs uppercase tracking-widest text-text-secondary">
        This format needs the full player
      </p>
      <RouterLink
        :to="detailHref"
        class="border border-[#f4f1e8]/35 bg-black/45 px-4 py-2 font-mono text-xs uppercase tracking-widest text-[#f4f1e8] no-underline transition-colors duration-150 hover:border-ink hover:text-ink"
      >
        Open in detail →
      </RouterLink>
    </div>

    <!-- Tap-to-play fallback (autoplay rejection). -->
    <button
      v-if="needsTapToPlay && detail"
      type="button"
      class="absolute inset-0 flex cursor-pointer items-center justify-center bg-transparent"
      :aria-label="`Play ${clip.title}`"
      @click="handleTapToPlay"
    >
      <span
        class="inline-flex h-16 w-16 items-center justify-center border border-white/25 bg-black/55 text-white"
      >
        <IconPlay :size="26" />
      </span>
    </button>

    <!-- Top band — title + game name on a solid legibility band (no gradient scrims). -->
    <div
      class="pointer-events-none absolute inset-x-0 top-0 flex flex-col gap-1 bg-black/60 px-4 py-3 text-white"
    >
      <p
        v-if="clip.game"
        class="m-0 font-mono text-[10px] uppercase tracking-[0.12em] text-white/70"
      >
        {{ clip.game.tag }}
      </p>
      <h2 class="m-0 line-clamp-2 font-heading text-base font-bold uppercase tracking-[0.01em]">
        {{ clip.title }}
      </h2>
    </div>

    <!-- Bottom band — author handle. -->
    <RouterLink
      :to="authorHref"
      class="pointer-events-auto absolute inset-x-0 bottom-0 flex items-center gap-2 bg-black/60 px-4 py-3 text-white no-underline"
    >
      <UserAvatar :user="clip.author" :size="32" />
      <AuthorHandle :username="clip.author.username" class="text-sm text-ink" />
    </RouterLink>

    <!-- Right-rail actions. -->
    <div class="absolute right-3 bottom-24 flex flex-col items-center gap-4 text-white">
      <button
        type="button"
        class="flex cursor-pointer flex-col items-center gap-1 bg-transparent disabled:opacity-60"
        :disabled="likeBusy"
        :aria-label="liked ? 'Unlike' : 'Like'"
        :aria-pressed="liked"
        @click="toggleLike"
      >
        <span
          class="inline-flex h-11 w-11 items-center justify-center border"
          :class="
            liked ? 'border-ink bg-ink text-signal-text' : 'border-white/25 bg-black/45 text-white'
          "
        >
          <IconHeart :size="20" />
        </span>
        <span class="font-mono text-[11px] tabular-nums">{{ formatNum(likeCount) }}</span>
      </button>

      <button
        type="button"
        class="flex cursor-pointer flex-col items-center gap-1 bg-transparent"
        :aria-label="globalMuted ? 'Unmute' : 'Mute'"
        :aria-pressed="!globalMuted"
        @click="onToggleMute"
      >
        <span
          class="inline-flex h-11 w-11 items-center justify-center border border-white/25 bg-black/45"
        >
          <IconVolumeMute v-if="globalMuted" :size="18" />
          <IconVolume v-else :size="18" />
        </span>
      </button>

      <button
        type="button"
        class="flex cursor-pointer flex-col items-center gap-1 bg-transparent text-white"
        aria-label="Open comments"
        :aria-expanded="commentsOpen"
        @click="openComments"
      >
        <span
          class="inline-flex h-11 w-11 items-center justify-center border border-white/25 bg-black/45"
        >
          <IconMessageCircle :size="18" />
        </span>
      </button>
    </div>

    <!-- Comments bottom sheet — teleported to body so it sits above the sticky
         nav and any other in-flow z-stacks. Backdrop dismisses; Esc handled
         here too so keyboard users can close without grabbing the X button.
         The video keeps playing behind the sheet; users can mute via the
         right-rail button if they want quiet reading. -->
    <Teleport to="body">
      <Transition
        enter-active-class="transition-opacity duration-150"
        leave-active-class="transition-opacity duration-150"
        enter-from-class="opacity-0"
        leave-to-class="opacity-0"
      >
        <div v-if="commentsOpen" class="fixed inset-0 z-50 bg-black/55" @click.self="closeComments">
          <Transition
            enter-active-class="transition-transform duration-200 ease-out"
            leave-active-class="transition-transform duration-150 ease-in"
            enter-from-class="translate-y-full"
            leave-to-class="translate-y-full"
            appear
          >
            <div
              v-if="commentsOpen"
              class="absolute inset-x-0 bottom-0 flex max-h-[75vh] flex-col border-t border-border-strong bg-surface-base"
              role="dialog"
              aria-label="Comments"
              @click.stop
            >
              <div class="mx-auto mt-2 h-1 w-10 shrink-0 bg-border-strong" aria-hidden="true"></div>
              <div class="flex shrink-0 items-center justify-between gap-3 px-4 py-3">
                <h2
                  class="m-0 font-heading text-base font-bold uppercase tracking-[0.04em] text-text-primary"
                >
                  Comments
                </h2>
                <div class="flex items-center gap-2">
                  <RouterLink
                    :to="detailHref"
                    class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-secondary no-underline transition-colors hover:text-ink"
                  >
                    View full clip →
                  </RouterLink>
                  <button
                    type="button"
                    class="inline-flex h-8 w-8 cursor-pointer items-center justify-center border border-border bg-transparent text-text-secondary transition-colors hover:border-ink hover:text-ink"
                    aria-label="Close comments"
                    @click="closeComments"
                  >
                    <IconX :size="14" />
                  </button>
                </div>
              </div>
              <div class="min-h-0 flex-1 overflow-y-auto px-4 pb-4">
                <CommentsSection :clip-id="clip.id" />
              </div>
            </div>
          </Transition>
        </div>
      </Transition>
    </Teleport>
  </article>
</template>
