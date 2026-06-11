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
import { issueNumber } from '@/lib/issue'
import { useAuthStore } from '@/stores/auth'
import { safeImageUrl } from '@/lib/url'
import TagChip from '@/components/TagChip.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import BroadcastFrame from '@/components/BroadcastFrame.vue'
import TelemetryStrip, { type TelemetryCell } from '@/components/TelemetryStrip.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import ClipCard from '@/components/ClipCard.vue'
import ClipEditDialog from '@/components/ClipEditDialog.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ReportDialog from '@/components/ReportDialog.vue'
import CommentsSection from '@/components/CommentsSection.vue'
import IconHeart from '@/components/icons/IconHeart.vue'
import IconShare from '@/components/icons/IconShare.vue'
import IconLink from '@/components/icons/IconLink.vue'
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
// bounce the owner straight to not-found, we treat a 404 as "maybe still processing" and poll a
// few times before giving up.
const processing = ref(false)
const PROCESSING_POLL_MS = 2500
const MAX_PROCESSING_POLLS = 12 // ~30s — short clips finish well within this
let processingPolls = 0
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
  () => clip.value?.id,
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

// Broadcast topbar right cell — best-effort spec line from stored metadata.
const playerSpec = computed(() => {
  const c = clip.value
  if (!c) return undefined
  const parts: string[] = []
  if (c.height) parts.push(`${c.height}p`)
  parts.push((c.videoCodec ?? 'h264').toUpperCase())
  return parts.join(' · ')
})

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
    { key: 'filed', label: 'Filed', value: formatRelativeTime(c.createdAt) },
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
    processingPolls = 0
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
      // missing — the detail endpoint can't tell them apart. Poll a few times showing a
      // processing state before falling back to not-found.
      if (processingPolls < MAX_PROCESSING_POLLS) {
        processingPolls++
        processing.value = true
        loading.value = false
        processingTimer = setTimeout(() => {
          if (myLoadId === latestLoadId) loadClip(true)
        }, PROCESSING_POLL_MS)
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
    setupPlayer(detail, el)
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

// Decide how to play a clip. If the browser can decode the stored master directly (H.264
// always; AV1 only on capable devices), play it as a plain progressive file. Otherwise fall
// back to a just-in-time H.264 HLS stream the server transcodes on demand.
function setupPlayer(detail: ClipDetail, el: HTMLVideoElement) {
  if (canPlayMaster(detail.videoCodec, el)) {
    el.src = detail.videoUrl
    player = new Plyr(el, { controls: BASE_CONTROLS, tooltips: { controls: true, seek: true } })
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
    })
    return
  }

  // No native HLS and no MSE — nothing left to try.
  failJitPlayback()
}

function teardownPlayer() {
  detachViewTracking()
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

const editOpen = ref(false)
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

// Owner-only kebab (Edit + Delete). KebabMenu owns open/close + outside-click + Esc; this
// view just declares the items.
const ownerMenuItems = computed<KebabMenuItem[]>(() => [
  { label: 'Edit', onClick: openEdit },
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
  <div class="mx-auto max-w-350 px-8 pt-10 pb-30 max-tablet:px-4">
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
        class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
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
        <router-link to="/" class="transition-colors hover:text-ink">Feed</router-link>
        <span>/</span>
        <span>No. {{ issueNumber(clip.id) }}</span>
      </div>

      <!-- Broadcast frame — the watch surface wears the HUD. -->
      <BroadcastFrame
        :channel="`FEED · NO. ${issueNumber(clip.id)}`"
        status="LIVE FROM ARCHIVE"
        live
        :spec="playerSpec"
      >
        <div class="border border-border bg-surface-sunken">
          <video ref="videoEl" controls playsinline class="block aspect-video w-full"></video>
        </div>
      </BroadcastFrame>

      <!-- Telemetry strip — like lives on the stat itself. -->
      <TelemetryStrip class="mt-6" :cells="telemetryCells" @cell-click="onTelemetryClick" />

      <!-- Meta block -->
      <div class="mt-7">
        <div class="flex items-start gap-5">
          <span
            class="font-heading text-[56px] font-bold leading-[0.92] tracking-[-0.01em] text-ink max-tablet:text-[40px]"
            aria-hidden="true"
          >
            {{ issueNumber(clip.id) }}
          </span>
          <div class="min-w-0">
            <h1
              class="m-0 font-heading text-[34px] font-bold leading-[1.05] uppercase tracking-[0.01em] text-text-primary max-tablet:text-[26px]"
            >
              {{ clip.title }}
            </h1>
            <div
              class="mt-2.5 flex flex-wrap items-center gap-x-2 gap-y-1 font-mono text-[11px] uppercase tracking-[0.1em] text-text-muted"
            >
              <AuthorHandle :username="clip.author.username" as="link" class="text-ink" />
              <span>· filed {{ formatRelativeTime(clip.createdAt) }}</span>
              <template v-if="clip.game">
                <span>·</span>
                <RouterLink
                  :to="{ name: 'game-detail', params: { slug: clip.game.slug } }"
                  class="transition-colors hover:text-ink"
                >
                  {{ clip.game.name }}
                </RouterLink>
              </template>
            </div>
          </div>
        </div>

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
          class="mt-3 inline-flex items-center gap-1.5 border border-border px-3 py-1 font-mono text-[11px] uppercase tracking-[0.06em] text-text-muted transition-colors hover:border-ink hover:text-ink"
        >
          <IconLink :size="12" />
          <span>Imported from {{ importSourceHost }}</span>
        </a>

        <div class="mt-5 flex flex-wrap items-center gap-3">
          <!-- Author info -->
          <div class="flex items-center gap-2.5">
            <span
              class="inline-flex h-9 w-9 shrink-0 items-center justify-center overflow-hidden font-mono text-xs font-semibold text-white"
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
                class="text-[13px] text-ink"
              />
              <div class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">
                Uploaded {{ formatRelativeTime(clip.createdAt) }} ago
              </div>
            </div>
          </div>

          <div class="flex-1" />

          <!-- Action buttons — bordered, counts on them -->
          <div class="flex items-center gap-2">
            <button
              class="flex cursor-pointer items-center gap-2 border bg-transparent px-4 py-2.5 transition-colors duration-150 disabled:opacity-60"
              :class="
                liked
                  ? 'border-ink text-ink'
                  : 'border-border text-text-primary hover:border-ink hover:text-ink'
              "
              :disabled="likeBusy"
              @click="toggleLike"
            >
              <IconHeart :size="15" />
              <span class="font-heading text-[15px] font-bold leading-none">{{
                formatNum(likeCount)
              }}</span>
              <span class="font-mono text-[10px] uppercase tracking-[0.12em]">Like</span>
            </button>

            <button
              class="flex cursor-pointer items-center gap-2 border border-border bg-transparent px-4 py-2.5 text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
              @click="handleShare"
            >
              <IconShare :size="15" />
              <span class="font-mono text-[10px] uppercase tracking-[0.12em]">Share</span>
            </button>

            <button
              v-if="auth.isAuthenticated && !isOwner"
              class="flex cursor-pointer items-center gap-1.5 border border-border bg-transparent px-4 py-2.5 font-mono text-[10px] uppercase tracking-[0.12em] text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
              @click="reportOpen = true"
            >
              <span>Report</span>
            </button>

            <KebabMenu
              v-if="isOwner"
              :items="ownerMenuItems"
              icon-orientation="vertical"
              trigger-variant="outlined"
            />
          </div>
        </div>
      </div>

      <!-- Description -->
      <div v-if="clip.description" class="mt-7 max-w-[64ch] border-t border-border pt-4">
        <div class="mb-2 font-mono text-[10px] uppercase tracking-[0.22em] text-text-secondary">
          Description
        </div>
        <p class="m-0 text-sm leading-[1.6] text-text-secondary">{{ clip.description }}</p>
      </div>

      <!-- Recommended — same game, current clip excluded -->
      <section v-if="recommended.length && clip.game" class="mt-10">
        <SectionHeader roman="II" kicker="Recommended" :title="`More ${clip.game.name}`" />
        <div class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-x-5.5 gap-y-7 pt-6">
          <ClipCard
            v-for="rec in recommended"
            :key="rec.id"
            :clip="rec"
            @click="router.push({ name: 'clip', params: { id: rec.id } })"
          />
        </div>
      </section>

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
        class="fixed bottom-6 left-1/2 z-9999 flex -translate-x-1/2 items-center gap-2 border border-ink bg-surface-raised px-4 py-3 font-mono text-[13px] tracking-[0.04em] whitespace-nowrap text-text-primary"
      >
        {{ toastText }}
      </div>
    </Transition>
  </div>
</template>
