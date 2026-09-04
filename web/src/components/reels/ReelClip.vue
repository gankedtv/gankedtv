<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { clips, type ClipDetail, type ClipFeedItem } from '@/api/clips'
import { useAuthStore } from '@/stores/auth'
import { formatNum } from '@/lib/format'
import type { CropRect } from '@/lib/crop'
import { contentFillTransform, detectPosterBars } from '@/lib/letterbox'
import UserAvatar from '@/components/UserAvatar.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import CommentsSection from '@/components/CommentsSection.vue'
import IconHeart from '@/components/icons/IconHeart.vue'
import IconVolume from '@/components/icons/IconVolume.vue'
import IconVolumeMute from '@/components/icons/IconVolumeMute.vue'
import IconMessageCircle from '@/components/icons/IconMessageCircle.vue'
import IconX from '@/components/icons/IconX.vue'
import IconPlay from '@/components/icons/IconPlay.vue'
import ThumbImage from '@/components/ThumbImage.vue'

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
const slotEl = useTemplateRef<HTMLElement>('slotEl')
const needsTapToPlay = ref(false)
const isPaused = ref(false)
const boosting = ref(false)
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
      el.playbackRate = 1
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
      // A skim must not survive the scroll that ends it.
      clearHold()
      endBoost()
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

// --- Black-bar reframing ------------------------------------------------------
// A master with bars baked in pays twice in reels: once for its own bars, once for the
// letterboxing a 9:16 column adds to landscape footage, and the clip ends up a stamp in the
// middle of the screen. Detection runs off the poster (see lib/letterbox) and only ever changes
// how the master is framed on screen — the clip itself is untouched.

const contentRect = ref<CropRect | null>(null)
const slotSize = ref({ w: 0, h: 0 })

// Guards against a late detection landing on a slot whose clip has since swapped.
let barsRequestId = 0

watch(
  () => props.clip.thumbnailUrl,
  (url) => {
    const myId = ++barsRequestId
    contentRect.value = null
    if (!url) return
    void detectPosterBars(url).then((rect) => {
      if (myId === barsRequestId) contentRect.value = rect
    })
  },
  { immediate: true },
)

function measureSlot() {
  const el = slotEl.value
  if (!el) return
  slotSize.value = { w: el.clientWidth, h: el.clientHeight }
}

onMounted(() => {
  measureSlot()
  if (typeof window !== 'undefined') window.addEventListener('resize', measureSlot)
})

onBeforeUnmount(() => {
  if (typeof window !== 'undefined') window.removeEventListener('resize', measureSlot)
})

// Zoom the master past its baked-in bars so the real content fills the reels column instead of
// being letterboxed twice. The bars this pushes outside the slot are already clipped by the
// article's overflow-hidden.
const videoStyle = computed(() => {
  const rect = contentRect.value
  const detail = props.detail
  const { w, h } = slotSize.value
  if (!rect || !detail) return undefined
  const transform = contentFillTransform(rect, detail.width, detail.height, w, h)
  return transform ? { transform } : undefined
})

// --- Pause + hold-to-skim -----------------------------------------------------
// Tap toggles playback; press-and-hold runs at 2x while held. The hold is cancelled by any
// meaningful pointer movement, because in a vertical snap feed most presses are the start of a
// scroll, not a request to skim.

const BOOST_RATE = 2
const HOLD_MS = 220
const HOLD_MOVE_TOLERANCE = 12

let holdTimer: ReturnType<typeof setTimeout> | null = null
let holdOrigin: { x: number; y: number } | null = null
let pointerMovedAway = false

function clearHold() {
  if (holdTimer !== null) {
    clearTimeout(holdTimer)
    holdTimer = null
  }
  holdOrigin = null
}

function endBoost() {
  if (!boosting.value) return
  boosting.value = false
  if (videoEl.value) videoEl.value.playbackRate = 1
}

function togglePlayback() {
  const el = videoEl.value
  if (!el) return
  if (el.paused) {
    Promise.resolve(el.play())
      .then(() => {
        needsTapToPlay.value = false
      })
      .catch(() => {
        needsTapToPlay.value = true
      })
  } else {
    el.pause()
  }
}

function onPointerDown(e: PointerEvent) {
  if (!props.isActive) return
  pointerMovedAway = false
  holdOrigin = { x: e.clientX, y: e.clientY }
  // Capture so a press that drifts off the button still delivers its up/cancel here; without it
  // a boost could be left running with nothing to switch it off.
  const target = e.currentTarget as Element & { setPointerCapture?: (id: number) => void }
  try {
    target.setPointerCapture?.(e.pointerId)
  } catch {
    // Capture is an optimisation, not a requirement.
  }
  holdTimer = setTimeout(() => {
    holdTimer = null
    const el = videoEl.value
    // Holding a paused clip shouldn't silently speed it up; the release toggles play instead.
    if (!el || el.paused) return
    boosting.value = true
    el.playbackRate = BOOST_RATE
  }, HOLD_MS)
}

function onPointerMove(e: PointerEvent) {
  if (!holdOrigin) return
  const dx = e.clientX - holdOrigin.x
  const dy = e.clientY - holdOrigin.y
  if (Math.hypot(dx, dy) < HOLD_MOVE_TOLERANCE) return
  pointerMovedAway = true
  clearHold()
  endBoost()
}

function onPointerUp() {
  const wasBoosting = boosting.value
  clearHold()
  endBoost()
  // A release that ends a skim is not also a pause request.
  if (wasBoosting || pointerMovedAway) return
  togglePlayback()
}

function onPointerCancel() {
  // Fired when the browser takes the gesture over for a scroll.
  pointerMovedAway = true
  clearHold()
  endBoost()
}

// Pointer releases already toggle; this catches keyboard activation only, which reports no
// click count. Without the guard every tap would toggle twice.
function onPlaybackClick(e: MouseEvent) {
  if (e.detail === 0) togglePlayback()
}

const showPlayBadge = computed(() => needsTapToPlay.value || (isPaused.value && props.isActive))

const playbackLabel = computed(() =>
  showPlayBadge.value ? `Play ${props.clip.title}` : `Pause ${props.clip.title}`,
)

// --- Interactions -------------------------------------------------------------

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
    ref="slotEl"
    class="relative flex h-full w-full items-center justify-center overflow-hidden bg-black"
  >
    <!-- Thumbnail layer — visible until the video element mounts. `eager`: this is the whole
         slot the viewer is looking at, so deferring it would defer the only thing on screen. -->
    <ThumbImage
      v-if="!detail"
      :src="clip.thumbnailUrl"
      :alt="clip.title"
      eager
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
      :style="videoStyle"
      playsinline
      preload="metadata"
      class="block max-h-full max-w-full object-contain"
      @play="isPaused = false"
      @pause="isPaused = true"
    />

    <!-- Delayed loading ticker. -->
    <div
      v-if="spinnerVisible && !detail && !detailErrored"
      class="pointer-events-none absolute inset-0 flex items-center justify-center"
    >
      <span class="block h-1.5 w-5.5 overflow-hidden rounded-full bg-white/15">
        <span
          class="block h-full w-full origin-left bg-accent animate-[tick_1.6s_ease-in-out_infinite]"
        ></span>
      </span>
    </div>

    <!-- Per-slot detail load error. -->
    <div
      v-if="detailErrored"
      class="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-black/60 text-center"
    >
      <p class="m-0 text-[10px] font-bold uppercase tracking-[0.14em] text-accent">
        Couldn't load video
      </p>
      <button
        type="button"
        class="cursor-pointer rounded-lg border border-white/20 bg-black/60 px-4 py-2 text-xs font-semibold text-[#f4f1e8] transition-colors duration-150 hover:border-accent hover:text-accent"
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
      class="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-black/60 px-6 text-center"
    >
      <p class="m-0 text-sm text-[#f4f1e8]/80">This format needs the full player</p>
      <RouterLink
        :to="detailHref"
        class="rounded-lg border border-white/20 bg-black/60 px-4 py-2 text-xs font-semibold text-[#f4f1e8] no-underline transition-colors duration-150 hover:border-accent hover:text-accent"
      >
        Open in detail →
      </RouterLink>
    </div>

    <!-- Playback surface: tap toggles pause, press-and-hold skims at 2x. Also carries the
         tap-to-play recovery when autoplay is rejected. Sits ahead of the right rail and the
         bottom overlay in DOM order, so those keep taking their own clicks. -->
    <button
      v-if="detail && !codecUnsupported"
      type="button"
      class="absolute inset-0 flex cursor-pointer items-center justify-center bg-transparent"
      :aria-label="playbackLabel"
      @pointerdown="onPointerDown"
      @pointermove="onPointerMove"
      @pointerup="onPointerUp"
      @pointercancel="onPointerCancel"
      @click="onPlaybackClick"
    >
      <span
        v-if="showPlayBadge"
        class="inline-flex size-16 items-center justify-center rounded-full border border-white/25 bg-black/55 text-[#f4f1e8]"
      >
        <IconPlay :size="26" />
      </span>
    </button>

    <!-- Skim indicator. Purely informational, so it never intercepts the hold that spawned it. -->
    <div
      v-if="boosting"
      class="pointer-events-none absolute inset-x-0 top-4 flex justify-center"
      aria-hidden="true"
    >
      <span
        class="rounded-full border border-white/20 bg-black/60 px-3 py-1 text-[10px] font-bold uppercase tracking-[0.14em] text-[#f4f1e8]"
      >
        2x speed
      </span>
    </div>

    <!-- Bottom legibility gradient — sanctioned overlay for text over video. -->
    <div
      class="pointer-events-none absolute inset-x-0 bottom-0 h-[40%] bg-[linear-gradient(transparent,rgba(0,0,0,0.88))]"
    ></div>

    <!-- Bottom overlay — game tag, title, author. Literal light colors: text
         over video never themes. -->
    <div
      class="pointer-events-none absolute inset-x-0 bottom-0 flex flex-col items-start gap-1.5 px-4 pb-4 pr-16"
    >
      <span
        v-if="clip.game"
        class="inline-flex items-center rounded-sm border border-accent-border bg-[rgba(0,229,160,0.16)] px-1.5 py-1 text-[9px] font-bold uppercase leading-none tracking-[0.07em] text-accent"
      >
        {{ clip.game.tag }}
      </span>
      <h2 class="m-0 line-clamp-2 text-sm font-semibold text-[#f4f1e8]">
        {{ clip.title }}
      </h2>
      <RouterLink :to="authorHref" class="pointer-events-auto flex items-center gap-2 no-underline">
        <UserAvatar :user="clip.author" :size="32" />
        <AuthorHandle :username="clip.author.username" class="text-xs font-semibold text-accent" />
      </RouterLink>
    </div>

    <!-- Right-rail actions. -->
    <div class="absolute right-3 bottom-24 flex flex-col items-center gap-4 text-[#f4f1e8]">
      <button
        type="button"
        class="flex cursor-pointer flex-col items-center gap-1 bg-transparent disabled:opacity-60"
        :disabled="likeBusy"
        :aria-label="liked ? 'Unlike' : 'Like'"
        :aria-pressed="liked"
        @click="toggleLike"
      >
        <span
          class="inline-flex size-11 items-center justify-center rounded-full border"
          :class="
            liked
              ? 'border-accent bg-accent text-[#080f0d]'
              : 'border-white/20 bg-black/40 text-[#f4f1e8]'
          "
        >
          <IconHeart :size="20" />
        </span>
        <span class="text-[10px] font-semibold tabular-nums text-[#f4f1e8]">{{
          formatNum(likeCount)
        }}</span>
      </button>

      <button
        type="button"
        class="flex cursor-pointer flex-col items-center gap-1 bg-transparent"
        :aria-label="globalMuted ? 'Unmute' : 'Mute'"
        :aria-pressed="!globalMuted"
        @click="onToggleMute"
      >
        <span
          class="inline-flex size-11 items-center justify-center rounded-full border border-white/20 bg-black/40 text-[#f4f1e8]"
        >
          <IconVolumeMute v-if="globalMuted" :size="18" />
          <IconVolume v-else :size="18" />
        </span>
      </button>

      <button
        type="button"
        class="flex cursor-pointer flex-col items-center gap-1 bg-transparent"
        aria-label="Open comments"
        :aria-expanded="commentsOpen"
        @click="openComments"
      >
        <span
          class="inline-flex size-11 items-center justify-center rounded-full border border-white/20 bg-black/40 text-[#f4f1e8]"
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
              class="absolute inset-x-0 bottom-0 flex max-h-[75vh] flex-col rounded-t-lg border-t border-border-strong bg-surface-base"
              role="dialog"
              aria-label="Comments"
              @click.stop
            >
              <div
                class="mx-auto mt-2 h-1 w-10 shrink-0 rounded-full bg-border-strong"
                aria-hidden="true"
              ></div>
              <div class="flex shrink-0 items-center justify-between gap-3 px-4 py-3">
                <h2
                  class="m-0 font-condensed text-base font-bold uppercase tracking-[0.04em] text-text-primary"
                >
                  Comments
                </h2>
                <div class="flex items-center gap-2">
                  <RouterLink
                    :to="detailHref"
                    class="text-xs font-semibold text-text-secondary no-underline transition-colors hover:text-accent"
                  >
                    View full clip →
                  </RouterLink>
                  <button
                    type="button"
                    class="inline-flex size-8 cursor-pointer items-center justify-center rounded-full border border-border-strong bg-transparent text-text-secondary transition-colors hover:border-accent hover:text-accent"
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
