<script setup lang="ts">
import { ref, computed, watch, onBeforeUnmount, useTemplateRef } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Plyr from 'plyr'
import 'plyr/dist/plyr.css'
import Hls from 'hls.js'
import { ApiError } from '@/api/client'
import { clips, type ClipDetail, type ClipFeedItem } from '@/api/clips'
import { games } from '@/api/games'
import { formatNum, formatDuration, formatRelativeTime } from '@/lib/format'
import { useAuthStore } from '@/stores/auth'
import { safeImageUrl } from '@/lib/url'
import TagChip from '@/components/TagChip.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import GameTag from '@/components/GameTag.vue'
import TelemetryStrip, { type TelemetryCell } from '@/components/TelemetryStrip.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import ClipCard from '@/components/ClipCard.vue'
import ClipEditDialog from '@/components/ClipEditDialog.vue'
import ClipVideoEditDialog from '@/components/ClipVideoEditDialog.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ReportDialog from '@/components/ReportDialog.vue'
import CommentsSection from '@/components/CommentsSection.vue'
import RewyndLogo from '@/components/RewyndLogo.vue'
import IconHeart from '@/components/icons/IconHeart.vue'
import IconShare from '@/components/icons/IconShare.vue'
import IconLink from '@/components/icons/IconLink.vue'
import IconLock from '@/components/icons/IconLock.vue'
import IconScissors from '@/components/icons/IconScissors.vue'
import IconPlay from '@/components/icons/IconPlay.vue'
import IconVolumeMute from '@/components/icons/IconVolumeMute.vue'
import KebabMenu, { type KebabMenuItem } from '@/components/KebabMenu.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const clip = ref<ClipDetail | null>(null)
const loading = ref(false)
const errored = ref(false)
// Distinct copy for the two failure modes: a detail-load failure vs. a JIT transcode that
// hasn't finished / failed. The latter looks fine (thumbnail loaded) so a generic message is
// confusing.
const DEFAULT_ERROR = "Couldn't load this clip."
const JIT_ERROR = 'This clip is still being prepared for your device — try again in a moment.'
const errorMessage = ref(DEFAULT_ERROR)
// A freshly-uploaded clip 404s until the pipeline (thumbnail + compress) finishes. Rather than
// bounce the owner straight to not-found, we treat a 404 as "maybe still processing" and keep
// polling for a bounded window before giving up. The window is time-based (not a fixed poll
// count) and generous: when the GPU encoder is unavailable the pipeline falls back to a software
// transcode that can take minutes, and bouncing to not-found mid-transcode is the reported 404.
// Aligned with the upload wizard's ~5-min patience. Interval backs off so a long wait (or a
// genuinely-missing clip) doesn't hammer the endpoint.
const processing = ref(false)
const PROCESSING_POLL_MIN_MS = 2500
const PROCESSING_POLL_MAX_MS = 10_000
const PROCESSING_WINDOW_MS = 5 * 60_000
let processingStartedAt = 0
let processingTimer: ReturnType<typeof setTimeout> | null = null
const liked = ref(false)
const likeCount = ref(0)
const likeBusy = ref(false)
const showToast = ref(false)
const toastText = ref('')

const videoEl = useTemplateRef<HTMLVideoElement>('videoEl')
let player: Plyr | null = null
let hls: Hls | null = null
// Bumped on every teardown so an in-flight JIT poll loop knows to stop (clip switched away
// or component unmounted).
let playerToken = 0
// A representative AV1 codec string for capability detection.
const AV1_MIME = 'video/mp4; codecs="av01.0.05M.08"'
const JIT_POLL_MS = 2000
// Give up polling after ~3 minutes so a stuck/disabled transcoder doesn't poll forever.
const JIT_MAX_POLLS = 90

// Autoplay. Shown when the browser refused both the unmuted and the muted attempt — the only
// remaining path is a real user gesture, and Plyr's own play button is behind our overlay.
const needsTapToPlay = ref(false)
// Set when the muted retry is what got playback going, so we can offer a one-click unmute
// instead of leaving the viewer to hunt for the volume control.
const autoplayMuted = ref(false)
// A source that resolves long after navigation (the JIT ladder polls for up to ~3 minutes, and
// a still-processing clip for up to five) must not suddenly start playing at someone who has
// moved on. Beyond this, mount and wait for a click.
const AUTOPLAY_GRACE_MS = 10_000
let playerMountedAt = 0
let unmuteListener: { el: HTMLVideoElement; handler: () => void } | null = null

const BASE_CONTROLS = [
  'play-large',
  'play',
  'progress',
  'current-time',
  'mute',
  'volume',
  'settings',
  'fullscreen',
]

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

// Recommended band — same-game clips, current one excluded. Silent failure:
// the band just doesn't render; clip playback is never blocked on it.
const recommended = ref<ClipFeedItem[]>([])
watch(
  () => [clip.value?.id, clip.value?.game?.slug] as const,
  async () => {
    recommended.value = []
    const current = clip.value
    if (!current?.game) return
    try {
      const page = await games.clips(current.game.slug, { limit: 5 })
      if (clip.value?.id !== current.id) return
      recommended.value = page.items.filter((c) => c.id !== current.id).slice(0, 4)
    } catch {
      recommended.value = []
    }
  },
)

const telemetryCells = computed<TelemetryCell[]>(() => {
  const c = clip.value
  if (!c) return []
  return [
    { key: 'views', label: 'Views', value: formatNum(c.viewCount) },
    {
      key: 'likes',
      label: 'Likes',
      value: formatNum(likeCount.value),
      ink: liked.value,
      action: true,
    },
    {
      key: 'duration',
      label: 'Runtime',
      value: c.durationSecs !== null ? formatDuration(c.durationSecs) : '—',
    },
    { key: 'filed', label: 'Posted', value: formatRelativeTime(c.createdAt) },
  ]
})

function onTelemetryClick(key: string) {
  if (key === 'likes') void toggleLike()
}

// Attribution badge for clips ingested via POST /clips/import. The host is shown to the
// viewer instead of the full URL (which can be long + carries query params); the link still
// goes to the full original. Null when the clip wasn't imported or the URL is malformed.
const importSourceHost = computed(() => {
  const url = clip.value?.importSourceUrl
  if (!url) return null
  try {
    return new URL(url).host.replace(/^www\./, '')
  } catch {
    return null
  }
})

async function loadClip(isPoll = false) {
  const myLoadId = ++latestLoadId
  if (!isPoll) {
    loading.value = true
    processing.value = false
    processingStartedAt = 0
    clearProcessingTimer()
    clip.value = null
    errorMessage.value = DEFAULT_ERROR
    teardownPlayer()
  }
  errored.value = false
  try {
    const fetched = shareCode.value
      ? await clips.getByShareCode(shareCode.value)
      : await clips.getDetail(clipId.value!)
    if (myLoadId !== latestLoadId) return
    processing.value = false
    clip.value = fetched
    liked.value = fetched.likedByMe
    likeCount.value = fetched.likeCount
  } catch (err) {
    if (myLoadId !== latestLoadId) return
    if (err instanceof ApiError && err.status === 404) {
      // 404 here means "not ready yet" (just uploaded, still transcoding) OR genuinely
      // missing — the detail endpoint can't tell them apart. Show a processing state and keep
      // polling until the time window elapses, then fall back to not-found.
      const now = Date.now()
      if (processingStartedAt === 0) processingStartedAt = now
      const elapsed = now - processingStartedAt
      if (elapsed < PROCESSING_WINDOW_MS) {
        processing.value = true
        loading.value = false
        // Back off from PROCESSING_POLL_MIN_MS toward PROCESSING_POLL_MAX_MS as the wait grows.
        const delay = Math.min(PROCESSING_POLL_MAX_MS, PROCESSING_POLL_MIN_MS + elapsed / 20)
        processingTimer = setTimeout(() => {
          if (myLoadId === latestLoadId) loadClip(true)
        }, delay)
        return
      }
      router.replace({ name: 'not-found' })
      return
    }
    // A non-404 error during a processing poll must clear `processing` so the error panel
    // (rendered after the processing panel) actually shows instead of being masked.
    processing.value = false
    errored.value = true
  } finally {
    if (myLoadId === latestLoadId && !processing.value) loading.value = false
  }
}

function clearProcessingTimer() {
  if (processingTimer !== null) {
    clearTimeout(processingTimer)
    processingTimer = null
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
    playerMountedAt = Date.now()
    setupPlayer(detail, el)
    attachViewTracking(detail.id, el)
  },
  { flush: 'post' },
)

// Start playback without a click. Two attempts: first with whatever mute state Plyr restored
// from the viewer's last visit, then — if the browser refuses, which it will for audible
// playback without a prior gesture — muted. A third refusal falls back to the tap overlay.
//
// Plyr has to exist first: its `muted` setter resolves a non-boolean argument through
// localStorage, and its own build calls `muted = null`, so anything set on the element before
// construction can be silently reverted. Assigning an explicit boolean afterwards is the only
// ordering that holds.
async function tryAutoplay(el: HTMLVideoElement) {
  const myToken = playerToken
  needsTapToPlay.value = false
  autoplayMuted.value = false

  // Reduced motion asks for less movement, and an unrequested video is the canonical case.
  // A backgrounded tab gets the same treatment: audio out of a tab you aren't looking at.
  if (
    window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ||
    document.visibilityState !== 'visible' ||
    Date.now() - playerMountedAt > AUTOPLAY_GRACE_MS
  ) {
    return
  }

  // jsdom returns undefined from play(); real browsers return a Promise. Normalise so .catch
  // is always safe and the view can be tested without a media shim.
  try {
    await Promise.resolve(el.play())
    return
  } catch {
    if (myToken !== playerToken) return
  }

  try {
    el.muted = true
    await Promise.resolve(el.play())
    if (myToken !== playerToken) return
    autoplayMuted.value = true
    watchForUnmute(el)
  } catch {
    if (myToken !== playerToken) return
    el.muted = false
    needsTapToPlay.value = true
  }
}

function handleTapToPlay() {
  const el = videoEl.value
  if (!el) return
  Promise.resolve(el.play())
    .then(() => {
      needsTapToPlay.value = false
    })
    .catch(() => {
      // The gesture didn't help either — leave the overlay up.
    })
}

// Unmute on the media element rather than the Plyr wrapper: Plyr's `muted` setter resolves a
// non-boolean through localStorage and its build assigns `muted = null`, so the element is the
// unambiguous surface. Plyr's own control updates off the resulting `volumechange`.
function unmute() {
  autoplayMuted.value = false
  if (videoEl.value) videoEl.value.muted = false
}

// Plyr's own mute control is right there in the toolbar; using it should retire our badge too.
function watchForUnmute(el: HTMLVideoElement) {
  detachUnmuteWatch()
  const onVolumeChange = () => {
    if (!el.muted) {
      autoplayMuted.value = false
      detachUnmuteWatch()
    }
  }
  el.addEventListener('volumechange', onVolumeChange)
  unmuteListener = { el, handler: onVolumeChange }
}

function detachUnmuteWatch() {
  if (unmuteListener) {
    unmuteListener.el.removeEventListener('volumechange', unmuteListener.handler)
    unmuteListener = null
  }
}


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

// Decide how to play a clip. If the browser can decode the stored master directly (H.264
// always; AV1 only on capable devices), play it as a plain progressive file. Otherwise fall
// back to a just-in-time H.264 HLS stream the server transcodes on demand.
function setupPlayer(detail: ClipDetail, el: HTMLVideoElement) {
  if (canPlayMaster(detail.videoCodec, el)) {
    el.src = detail.videoUrl
    player = new Plyr(el, { controls: BASE_CONTROLS, tooltips: { controls: true, seek: true } })
    void tryAutoplay(el)
    return
  }
  void startJitStream(detail.id, el)
}

function canPlayMaster(codec: string | null, el: HTMLVideoElement): boolean {
  if (!codec || codec === 'h264') return true
  if (codec === 'av1') return el.canPlayType(AV1_MIME) !== ''
  // Unknown codec: optimistically try direct playback rather than forcing a transcode.
  return true
}

// Poll the JIT stream endpoint until a cached H.264 ladder is ready, then attach it. The
// captured playerToken aborts the loop if the user navigates away or the component unmounts.
async function startJitStream(id: string, el: HTMLVideoElement) {
  const myToken = playerToken
  for (let polls = 0; polls < JIT_MAX_POLLS; polls++) {
    let res
    try {
      res = await clips.getStream(id)
    } catch {
      if (myToken === playerToken) failJitPlayback()
      return
    }
    if (myToken !== playerToken) return
    if (res.status === 'ready' && res.hlsUrl) {
      attachHlsStream(el, res.hlsUrl)
      return
    }
    await new Promise((r) => setTimeout(r, JIT_POLL_MS))
    if (myToken !== playerToken) return
  }
  // Exhausted the poll budget without a ready rendition — surface an error rather than hang.
  failJitPlayback()
}

// Surface a JIT-specific error. Retry (the error panel button) re-runs loadClip, which tears
// down the player and re-enters setupPlayer → startJitStream for a fresh attempt.
function failJitPlayback() {
  errorMessage.value = JIT_ERROR
  errored.value = true
}

// Attach an HLS stream: native on Safari, hls.js (with a Plyr quality menu) elsewhere.
function attachHlsStream(el: HTMLVideoElement, hlsUrl: string) {
  if (el.canPlayType('application/vnd.apple.mpegurl') !== '') {
    el.src = hlsUrl
    player = new Plyr(el, { controls: BASE_CONTROLS, tooltips: { controls: true, seek: true } })
    void tryAutoplay(el)
    return
  }

  if (Hls.isSupported()) {
    const instance = new Hls()
    hls = instance
    instance.loadSource(hlsUrl)
    instance.attachMedia(el)
    instance.on(Hls.Events.MANIFEST_PARSED, () => {
      // Highest-first list of distinct rendition heights for the quality menu.
      const heights = [...new Set(instance.levels.map((l) => l.height))].sort((a, b) => b - a)
      player = new Plyr(el, {
        controls: BASE_CONTROLS,
        tooltips: { controls: true, seek: true },
        // 0 = Plyr's "Auto" sentinel: let hls.js pick the level by bandwidth.
        quality: {
          default: 0,
          options: [0, ...heights],
          forced: true,
          onChange: (newQuality: number) => {
            if (newQuality === 0) {
              instance.currentLevel = -1 // -1 = ABR auto
              return
            }
            const levelIndex = instance.levels.findIndex((l) => l.height === newQuality)
            if (levelIndex !== -1) instance.currentLevel = levelIndex
          },
        },
        i18n: { qualityLabel: { 0: 'Auto' } },
      })
      void tryAutoplay(el)
    })
    return
  }

  // No native HLS and no MSE — nothing left to try.
  failJitPlayback()
}

function teardownPlayer() {
  detachViewTracking()
  detachUnmuteWatch()
  needsTapToPlay.value = false
  autoplayMuted.value = false
  // Invalidate any in-flight JIT poll loop.
  playerToken++
  if (player) {
    player.destroy()
    player = null
  }
  if (hls) {
    hls.destroy()
    hls = null
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
  clearProcessingTimer()
  if (toastTimer !== null) clearTimeout(toastTimer)
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

// Render the player at the clip's REAL aspect ratio rather than a hard-coded 16:9. Before crop
// existed every master was effectively widescreen, so aspect-video was harmless; a 21:9 or 9:16
// clip in a 16:9 box letterboxes on all four sides and undoes the crop the user just paid an
// encode for. width/height have been served all along with no consumers — this is their job.
//
// Inline aspect-ratio beats Plyr's height:auto, which resolves against the video's intrinsic
// size only after metadata loads (so the box jumps). Falls back to the aspect-video class for
// older rows whose dimensions the pipeline never recorded.
//
// The size cap ships as a CUSTOM PROPERTY, not as an inline max-width, and that is load-bearing:
// Plyr's stylesheet does `:fullscreen video{height:100%}`, so in fullscreen the video's height
// becomes definite and the only thing left constraining it is our cap. An inline cap wins over
// every stylesheet rule, so it would survive into fullscreen and hold the video at a fraction of
// the screen — black on all four sides. Handing the value to a class via a variable lets the
// `[:fullscreen_&]` rule below switch it off where it doesn't belong.
//
// Cap the WIDTH, not the height: `aspect-ratio` + `max-height` clamps the box without shrinking
// the width, so the ratio silently breaks and the content pillarboxes. width ≤ 75vh × ratio is
// the same constraint expressed on the axis that actually resizes. (Same trap as ClipCropper.)
const playerStyle = computed(() => {
  const w = clip.value?.width
  const h = clip.value?.height
  if (!w || !h || w <= 0 || h <= 0) return null
  return {
    aspectRatio: `${w} / ${h}`,
    '--clip-player-max-w': `${((75 * w) / h).toFixed(2)}vh`,
  }
})

const editOpen = ref(false)
const editVideoOpen = ref(false)
const deleteOpen = ref(false)
const deleting = ref(false)

const isOwner = computed(() => !!auth.user && !!clip.value && clip.value.author.id === auth.user.id)

// Report dialog state — only available to signed-in non-owners. The dialog component is the
// same one used by CommentsSection / UserView; this view just provides the trigger button.
const reportOpen = ref(false)
function onReportSubmitted() {
  reportOpen.value = false
  fireToast('Report submitted')
}

// ClipVideoEditDialog opens a second <video> on the same master, so without this two would play.
watch([editOpen, editVideoOpen, deleteOpen, reportOpen], (states) => {
  if (states.some(Boolean)) videoEl.value?.pause()
})

// Owner-only kebab (Edit + Trim & crop + Delete). KebabMenu owns open/close + outside-click +
// Esc; this view just declares the items. Trim and crop share ONE entry because they share one
// re-encode — separate entries would walk the owner through two of them for the same result.
const ownerMenuItems = computed<KebabMenuItem[]>(() => [
  { label: 'Edit', onClick: openEdit },
  { label: 'Trim & crop', onClick: openEditVideo },
  { label: 'Delete', variant: 'danger', onClick: openDelete },
])

function onSaved(updated: ClipDetail) {
  clip.value = updated
  fireToast('Clip updated')
}

function onEditError(message: string) {
  fireToast(message)
}

function openEdit() {
  // KebabMenu closes itself on item-click, so the trigger doesn't need to do it.
  editOpen.value = true
}

function openEditVideo() {
  editVideoOpen.value = true
}

// The clip has left 'ready', so the detail route 404s until the pipeline finishes. Reloading
// drops straight into the existing processing state, which polls until the re-cut lands.
function onVideoEdited() {
  fireToast('Re-cutting your clip…')
  void loadClip()
}

function openDelete() {
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
  <main class="mx-auto max-w-300 px-7 pt-7 pb-16 max-tablet:px-4">
    <!-- Loading -->
    <StatusPanel v-if="loading" kind="loading" message="Loading" />

    <!-- Still processing (freshly uploaded; transcoding) -->
    <StatusPanel
      v-else-if="processing"
      kind="loading"
      message="Processing your clip… this can take a moment."
    />

    <!-- Error -->
    <StatusPanel v-else-if="errored" kind="error" :message="errorMessage">
      <button
        class="cursor-pointer rounded-lg border border-border bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="(clipId || shareCode) && loadClip()"
      >
        Retry
      </button>
    </StatusPanel>

    <div v-else-if="clip">
      <!-- Player. The poster matters more now that playback starts on its own: a blocked
           autoplay shows the thumbnail rather than a black box. -->
      <div class="relative overflow-hidden rounded-lg border border-border bg-black">
        <!-- The two fullscreen variants drop the in-page size cap: `:fullscreen` covers the
             native path, `.plyr--fullscreen-fallback` covers Plyr's own fallback when the
             Fullscreen API isn't available. Without them the video can't fill the screen. -->
        <video
          ref="videoEl"
          :poster="clip.thumbnailUrl"
          controls
          playsinline
          preload="auto"
          :style="playerStyle"
          :class="[
            'block w-full',
            playerStyle
              ? 'mx-auto max-w-[var(--clip-player-max-w)] [.plyr--fullscreen-fallback_&]:max-w-none [:fullscreen_&]:max-w-none'
              : 'aspect-video',
          ]"
        ></video>

        <!-- Autoplay was refused outright — offer the gesture the browser is waiting for. -->
        <button
          v-if="needsTapToPlay"
          type="button"
          class="absolute inset-0 flex cursor-pointer items-center justify-center bg-transparent"
          :aria-label="`Play ${clip.title}`"
          @click="handleTapToPlay"
        >
          <span
            class="inline-flex size-16 items-center justify-center rounded-full border border-white/25 bg-black/55 text-[#f4f1e8]"
          >
            <IconPlay :size="26" />
          </span>
        </button>

        <!-- Playing, but only because we muted it to get past the autoplay policy. -->
        <button
          v-else-if="autoplayMuted"
          type="button"
          class="absolute left-3 top-3 inline-flex cursor-pointer items-center gap-1.5 rounded-lg border border-white/25 bg-black/55 px-2.5 py-1.5 text-[11px] font-semibold text-[#f4f1e8] transition-colors duration-150 hover:border-accent hover:text-accent"
          @click="unmute"
        >
          <IconVolumeMute :size="13" />
          Unmute
        </button>
      </div>

      <!-- Meta block -->
      <div class="mt-5">
        <div v-if="clip.game" class="flex flex-wrap items-center gap-2">
          <GameTag :tag="clip.game.tag" size="md" />
          <RouterLink
            :to="{ name: 'game-detail', params: { slug: clip.game.slug } }"
            class="text-[11px] font-semibold text-text-secondary transition-colors duration-150 hover:text-accent"
          >
            {{ clip.game.name }}
          </RouterLink>
        </div>
        <h1
          class="m-0 mt-2 font-condensed text-[22px] font-extrabold uppercase leading-tight text-text-primary"
        >
          {{ clip.title }}
        </h1>

        <!-- Verified provenance: 'api' uploads came straight from the author's device via
             their device-approved API key (rewynd) — visible to every viewer. -->
        <div
          v-if="clip.uploadSource === 'api'"
          class="mt-3 mr-2 inline-flex items-center gap-1.5 rounded-lg border border-accent-border bg-accent-bg px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.06em] text-accent"
          title="Uploaded straight from rewynd on the author's device, with an API key they approved"
        >
          <RewyndLogo :size="12" />
          <span>rewynd verified</span>
        </div>

        <!-- Re-cut disclosure: the footage changed after publish, so every viewer sees it.
             Metadata-only edits deliberately don't set editedAt. -->
        <div
          v-if="clip.editedAt"
          class="mt-3 mr-2 inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted"
          :title="`The author re-cut this clip on ${new Date(clip.editedAt).toLocaleString()}`"
        >
          <IconScissors :size="12" />
          <span>Edited</span>
        </div>

        <!-- Owner-only visibility badge: reminds the uploader a clip isn't public. Other
             viewers never reach a private clip, and unlisted needs no callout for them. -->
        <div
          v-if="isOwner && clip.visibility !== 'public'"
          class="mt-3 inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted"
          :title="
            clip.visibility === 'private'
              ? 'Only you can see this clip'
              : 'Hidden from feeds, anyone with the link can watch'
          "
        >
          <IconLock v-if="clip.visibility === 'private'" :size="12" />
          <IconLink v-else :size="12" />
          <span>{{ clip.visibility === 'private' ? 'Private' : 'Unlisted' }}</span>
        </div>

        <!-- Author + action row -->
        <div class="mt-3 flex flex-wrap items-center gap-3">
          <div class="flex items-center gap-2.5">
            <span
              class="inline-flex h-9 w-9 shrink-0 items-center justify-center overflow-hidden rounded-full text-xs font-semibold text-white"
              :style="{ background: authorColor }"
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
                class="text-[13px] font-semibold text-accent"
              />
              <div class="text-[11px] text-text-muted">
                Uploaded {{ formatRelativeTime(clip.createdAt) }} ago
              </div>
            </div>
          </div>

          <div class="flex-1" />

          <div class="flex items-center gap-2">
            <button
              class="flex cursor-pointer items-center gap-1.5 rounded-lg border px-3 py-1.5 text-xs font-semibold transition-colors duration-150 disabled:opacity-60"
              :class="
                liked
                  ? 'border-accent-border bg-accent-bg text-accent'
                  : 'border-border text-text-secondary hover:border-border-strong hover:text-accent'
              "
              :disabled="likeBusy"
              @click="toggleLike"
            >
              <IconHeart :size="14" />
              <span>{{ formatNum(likeCount) }}</span>
              <span>Like</span>
            </button>

            <button
              class="flex cursor-pointer items-center gap-1.5 rounded-lg border border-border bg-transparent px-3 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-border-strong hover:text-text-primary"
              @click="handleShare"
            >
              <IconShare :size="14" />
              <span>Share</span>
            </button>

            <button
              v-if="auth.isAuthenticated && !isOwner"
              class="cursor-pointer rounded-lg border border-border bg-transparent px-3 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-border-strong hover:text-text-primary"
              @click="reportOpen = true"
            >
              Report
            </button>

            <KebabMenu
              v-if="isOwner"
              :items="ownerMenuItems"
              icon-orientation="vertical"
              trigger-variant="outlined"
            />
          </div>
        </div>

        <!-- Stats — like also lives on the stat itself -->
        <TelemetryStrip class="mt-4" :cells="telemetryCells" @cell-click="onTelemetryClick" />

        <!-- Description -->
        <p
          v-if="clip.description"
          class="m-0 mt-4 max-w-[64ch] text-[13px] leading-relaxed text-text-secondary"
        >
          {{ clip.description }}
        </p>

        <div v-if="clip.tags.length" class="mt-4 flex flex-wrap gap-2">
          <TagChip v-for="t in clip.tags" :key="t.id" :slug="t.slug" :name="t.name" size="md" />
        </div>

        <!-- Source attribution for imported clips. Clicking opens the original
             in a new tab so reviewers can confirm credit / spot reuploads at a glance. -->
        <a
          v-if="clip.importSourceUrl && importSourceHost"
          :href="clip.importSourceUrl"
          target="_blank"
          rel="noopener noreferrer"
          class="mt-4 inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-1.5 text-[11px] font-semibold text-text-muted transition-colors duration-150 hover:border-accent hover:text-accent"
        >
          <IconLink :size="12" />
          <span>Imported from {{ importSourceHost }}</span>
        </a>
      </div>

      <!-- Comments — component root carries its own top margin -->
      <CommentsSection :clip-id="clip.id" class="border-t border-border pt-7" />

      <!-- Recommended — same game, current clip excluded -->
      <section v-if="recommended.length && clip.game" class="mt-8 border-t border-border pt-7">
        <SectionHeader kicker="Recommended" title="More Clips" />
        <div class="grid grid-cols-4 gap-3.5 max-lg:grid-cols-2 max-tablet:grid-cols-1">
          <ClipCard
            v-for="rec in recommended"
            :key="rec.id"
            :clip="rec"
            @click="router.push({ name: 'clip', params: { id: rec.id } })"
          />
        </div>
      </section>
    </div>

    <ClipEditDialog
      v-if="clip"
      :clip="clip"
      :open="editOpen"
      @close="editOpen = false"
      @saved="onSaved"
      @error="onEditError"
    />

    <ClipVideoEditDialog
      v-if="clip"
      :clip="clip"
      :open="editVideoOpen"
      @close="editVideoOpen = false"
      @edited="onVideoEdited"
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

    <ReportDialog
      v-if="clip"
      :open="reportOpen"
      target-type="clip"
      :target-id="clip.id"
      @cancel="reportOpen = false"
      @submitted="onReportSubmitted"
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
        class="fixed bottom-6 left-1/2 z-9999 flex -translate-x-1/2 items-center gap-2 rounded-lg border border-border-strong bg-surface-raised px-4 py-3 text-[13px] whitespace-nowrap text-text-primary"
      >
        {{ toastText }}
      </div>
    </Transition>
  </main>
</template>
