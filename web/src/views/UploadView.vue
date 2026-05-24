<script setup lang="ts">
import { ref, computed, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { clips } from '@/api/clips'
import type { ClipStatus, GameSummary } from '@/api/clips'
import GameSelector from '@/components/GameSelector.vue'
import TagInput from '@/components/TagInput.vue'
import PageHeader from '@/components/PageHeader.vue'
import IconUploadCloud from '@/components/icons/IconUploadCloud.vue'
import IconFile from '@/components/icons/IconFile.vue'
import IconFileText from '@/components/icons/IconFileText.vue'
import IconArrowRight from '@/components/icons/IconArrowRight.vue'
import IconArrowLeft from '@/components/icons/IconArrowLeft.vue'
import IconGlobe from '@/components/icons/IconGlobe.vue'
import IconLink from '@/components/icons/IconLink.vue'

const router = useRouter()

// Guard against a missing/garbage env value: Number('foo') is NaN, and `f.size > NaN`
// is always false — silently disabling the size cap. Fall back to a safe default instead.
const MAX_UPLOAD_MB = (() => {
  const parsed = Number(String(import.meta.env.VITE_MAX_UPLOAD_SIZE_MB ?? '').trim())
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 500
})()
const MAX_UPLOAD_BYTES = MAX_UPLOAD_MB * 1024 * 1024

// 'upload' = the existing 3-step presigned-PUT flow (file picker → metadata → PUT + complete).
// 'import' = paste a Medal.tv / YouTube URL; the server fetches it and runs the same
// thumbnail → compress → ready pipeline that an upload would. UI shares the wizard frame.
type IngestMode = 'upload' | 'import'
const mode = ref<IngestMode>('upload')

type Step = 1 | 2 | 3
const step = ref<Step>(1)
const file = ref<File | null>(null)
// URL the user pasted in import mode. Client-side allow-list mirrors the server's
// MediaJobs:Import:AllowedHosts default — the server is still the source of truth (it
// re-validates and 400s on unsupported hosts), this is just to give immediate feedback.
const importUrl = ref('')
const IMPORT_ALLOWED_HOSTS = [
  'youtube.com',
  'www.youtube.com',
  'm.youtube.com',
  'youtu.be',
  'medal.tv',
  'www.medal.tv',
]
const isImportUrlValid = computed(() => {
  const raw = importUrl.value.trim()
  if (!raw) return false
  try {
    const u = new URL(raw)
    if (u.protocol !== 'https:') return false
    return IMPORT_ALLOWED_HOSTS.includes(u.host.toLowerCase())
  } catch {
    return false
  }
})

// Derives a public YouTube thumbnail URL from a watch / shorts / youtu.be link so the
// preview card on step 2 isn't empty for import-mode clips. Free + CORS-friendly because
// img.youtube.com serves these unauthenticated. Medal.tv has no equivalent public pattern
// — Medal previews stay blank (server fills in the thumbnail post-fetch anyway).
function extractYoutubeVideoId(raw: string): string | null {
  try {
    const u = new URL(raw.trim())
    const host = u.host.toLowerCase()
    if (host === 'youtu.be') {
      return u.pathname.slice(1).split('/')[0] || null
    }
    if (host.endsWith('youtube.com')) {
      // /watch?v=ID, /shorts/ID, /embed/ID.
      const v = u.searchParams.get('v')
      if (v) return v
      const parts = u.pathname.split('/').filter(Boolean)
      if ((parts[0] === 'shorts' || parts[0] === 'embed' || parts[0] === 'live') && parts[1]) {
        return parts[1]
      }
    }
    return null
  } catch {
    return null
  }
}
// Server-side metadata probe for the import URL: title + actual duration. Populated when
// the user clicks "Continue" in import mode; used to (a) gate the transition to step 2
// when the source is already too long and (b) prefill the title so step 2 isn't blank.
const previewLoading = ref(false)
const previewError = ref<string | null>(null)
const previewData = ref<import('@/api/clips').ImportClipPreview | null>(null)

// Client-side poster (data URL) captured from the picked file so the Preview card shows a real
// frame while the user fills in title/game/tags — no server round-trip. Best-effort: stays null
// if the browser can't decode the source.
const posterUrl = ref<string | null>(null)
// Monotonic token so a slow capture from an earlier pick can't overwrite a newer one's poster.
let posterRequestId = 0
// Cap the preview canvas so a 4K/8K source doesn't allocate a huge bitmap for a small thumbnail.
const MAX_PREVIEW_DIM = 1280
const title = ref('')
const desc = ref('')
const visibility = ref<'public' | 'unlisted'>('public')
const dragging = ref(false)

const selectedGame = ref<GameSummary | null>(null)
const selectedTags = ref<string[]>([])

// Upload state — granular so the checklist can light up step-by-step.
// Upload stages: idle → creating → uploading → completing → done. Import stages reuse the
// same machine: idle → submitting → importing → processing → done. 'error' is shared.
type UploadStage =
  | 'idle'
  | 'creating'
  | 'uploading'
  | 'completing'
  | 'submitting'
  | 'importing'
  | 'processing'
  | 'done'
  | 'error'
const stage = ref<UploadStage>('idle')
const uploadPct = ref(0)
const errorMsg = ref<string | null>(null)
const createdClipId = ref<string | null>(null)
const createdShareCode = ref<string | null>(null)
let activeXhr: XMLHttpRequest | null = null
let pollTimer: ReturnType<typeof setTimeout> | null = null

onUnmounted(() => {
  if (activeXhr) activeXhr.abort()
  if (pollTimer) clearTimeout(pollTimer)
})

// Toggling between upload / import mid-flow must not carry stale state from the other
// mode — e.g. the preview card would otherwise show the locally-picked file's poster
// after switching to URL import, or the URL string lingering after switching back.
watch(mode, (next) => {
  errorMsg.value = null
  if (next === 'import') {
    // Drop the local file + its derived poster + any in-flight XHR.
    if (activeXhr) {
      activeXhr.abort()
      activeXhr = null
    }
    file.value = null
    posterUrl.value = null
    posterRequestId++ // invalidate any pending capture
  } else {
    importUrl.value = ''
    posterUrl.value = null
    previewError.value = null
    previewData.value = null
    if (pollTimer) {
      clearTimeout(pollTimer)
      pollTimer = null
    }
  }
})

// In import mode, derive a YouTube thumbnail (when the URL parses to a video id) so the
// preview card mirrors the local-file flow. Non-YouTube URLs (Medal, malformed) leave the
// preview blank — server fills the real thumbnail in post-fetch.
watch(importUrl, (next) => {
  if (mode.value !== 'import') return
  const id = extractYoutubeVideoId(next)
  posterUrl.value = id ? `https://img.youtube.com/vi/${id}/hqdefault.jpg` : null
  // Stale preview info doesn't apply to a new URL — clear it. The user re-probes on the
  // next "Continue" click.
  previewError.value = null
  previewData.value = null
})

function pickFile(f: File | null) {
  if (!f) return
  // Bump on every pick (valid or not) so any in-flight capture from a prior pick is ignored.
  const requestId = ++posterRequestId
  if (!f.type.startsWith('video/')) {
    // Clear any prior valid selection — leaving the old file as the "current"
    // pick alongside an error about a different file is confusing.
    file.value = null
    posterUrl.value = null
    errorMsg.value = `Unsupported file type "${f.type || 'unknown'}" — pick a video.`
    return
  }
  if (f.size > MAX_UPLOAD_BYTES) {
    file.value = null
    posterUrl.value = null
    errorMsg.value = `File is ${formatSize(f.size)} — limit is ${MAX_UPLOAD_MB} MB.`
    return
  }
  errorMsg.value = null
  file.value = f
  void generatePoster(f, requestId)
}

// Capture a representative frame from the picked file via an offscreen <video> + <canvas>.
// Resolves to a JPEG data URL, or null if the browser can't decode/draw the source. Only
// assigns posterUrl if this is still the latest pick (requestId guard).
async function generatePoster(f: File, requestId: number): Promise<void> {
  posterUrl.value = null
  const objectUrl = URL.createObjectURL(f)
  try {
    const url = await capturePosterFrame(objectUrl)
    if (requestId === posterRequestId) posterUrl.value = url
  } catch {
    // best-effort — the preview just falls back to the filename
  } finally {
    URL.revokeObjectURL(objectUrl)
  }
}

function capturePosterFrame(src: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const video = document.createElement('video')
    video.muted = true
    video.preload = 'auto'
    video.src = src

    const cleanup = () => {
      video.removeAttribute('src')
      video.load()
    }

    video.onloadeddata = () => {
      // Seek a little in (or the midpoint of a very short clip) for a non-black frame.
      const target = Math.min(1, (Number.isFinite(video.duration) ? video.duration : 2) / 2)
      video.currentTime = Number.isFinite(target) ? target : 0
    }
    video.onseeked = () => {
      try {
        if (!video.videoWidth || !video.videoHeight) throw new Error('no video frame')
        // Downscale so a 4K/8K source doesn't allocate a giant bitmap for a small preview.
        const scale = Math.min(1, MAX_PREVIEW_DIM / Math.max(video.videoWidth, video.videoHeight))
        const canvas = document.createElement('canvas')
        canvas.width = Math.round(video.videoWidth * scale)
        canvas.height = Math.round(video.videoHeight * scale)
        const ctx = canvas.getContext('2d')
        if (!ctx) throw new Error('no 2d context')
        ctx.drawImage(video, 0, 0, canvas.width, canvas.height)
        resolve(canvas.toDataURL('image/jpeg', 0.7))
      } catch (err) {
        reject(err instanceof Error ? err : new Error('poster capture failed'))
      } finally {
        cleanup()
      }
    }
    video.onerror = () => {
      cleanup()
      reject(new Error('video decode failed'))
    }
  })
}

function handleFileSelect(e: Event) {
  const input = e.target as HTMLInputElement
  pickFile(input.files?.[0] ?? null)
  if (e.target instanceof HTMLInputElement) e.target.value = ''
}

function handleDrop(e: DragEvent) {
  dragging.value = false
  pickFile(e.dataTransfer?.files?.[0] ?? null)
}

function formatSize(bytes: number): string {
  if (bytes >= 1_073_741_824) return (bytes / 1_073_741_824).toFixed(1) + ' GB'
  if (bytes >= 1_048_576) return (bytes / 1_048_576).toFixed(1) + ' MB'
  return (bytes / 1024).toFixed(1) + ' KB'
}

// Generous ceiling so a 500 MB upload over a slow connection still finishes,
// but bounded so a fully-stalled connection doesn't spin forever.
const UPLOAD_TIMEOUT_MS = 10 * 60 * 1000

function putWithProgress(url: string, body: File, contentType: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    activeXhr = xhr
    xhr.open('PUT', url)
    xhr.timeout = UPLOAD_TIMEOUT_MS
    // Must match the Content-Type the server signed for, NOT the browser-detected
    // file MIME — S3 includes it in the signature and 403s on mismatch.
    xhr.setRequestHeader('Content-Type', contentType)
    xhr.upload.onprogress = (ev) => {
      if (ev.lengthComputable) uploadPct.value = (ev.loaded / ev.total) * 100
    }
    xhr.onload = () => {
      activeXhr = null
      if (xhr.status >= 200 && xhr.status < 300) resolve()
      else reject(new Error(`PUT failed: ${xhr.status}`))
    }
    xhr.onerror = () => {
      activeXhr = null
      reject(new Error('PUT network error'))
    }
    xhr.onabort = () => {
      activeXhr = null
      reject(new Error('PUT aborted'))
    }
    xhr.ontimeout = () => {
      activeXhr = null
      reject(new Error('Upload timed out — check your connection and try again'))
    }
    xhr.send(body)
  })
}

async function startUpload() {
  if (!file.value || !title.value.trim()) return
  step.value = 3
  stage.value = 'creating'
  uploadPct.value = 0
  errorMsg.value = null
  try {
    const created = await clips.create({
      title: title.value.trim(),
      description: desc.value.trim() || null,
      gameId: selectedGame.value?.id ?? null,
      visibility: visibility.value,
      // Omit the field entirely when empty so the server treats it as "no tags"
      // instead of an empty array. Same wire shape as the old POST.
      ...(selectedTags.value.length ? { tags: selectedTags.value } : {}),
    })
    createdClipId.value = created.id

    stage.value = 'uploading'
    const presigned = await clips.getUploadUrl(created.id)
    await putWithProgress(presigned.url, file.value, presigned.contentType)

    stage.value = 'completing'
    await clips.complete(created.id)

    stage.value = 'done'
    uploadPct.value = 100
  } catch (err) {
    stage.value = 'error'
    errorMsg.value = friendlyUploadError(err)
  }
}

function friendlyUploadError(err: unknown): string {
  if (err instanceof ApiError) {
    return `Server error (${err.status}). Please try again.`
  }
  if (err instanceof Error) {
    // Normalize the raw XHR errors from putWithProgress so users see actionable copy
    // instead of `PUT failed: 403`. Pre-formatted messages (e.g. the timeout) are
    // already user-friendly and pass through unchanged.
    const m = err.message
    if (m.startsWith('PUT failed') || m.startsWith('PUT network error')) {
      return 'Upload was interrupted. Please try again.'
    }
    if (m.startsWith('PUT aborted')) {
      return 'Upload cancelled.'
    }
    return m
  }
  return 'Upload failed.'
}

const progressLabel = computed(() => {
  switch (stage.value) {
    case 'uploading':
      return 'Uploading'
    case 'submitting':
      return 'Submitting'
    case 'importing':
      return 'Fetching source'
    case 'processing':
      return 'Processing'
    case 'done':
      return 'Done'
    case 'error':
      return 'Failed'
    default:
      return 'Preparing'
  }
})

const checklistDone = computed(() => {
  // Two parallel pipelines — upload (current 3-stage XHR flow) and import (submit + poll).
  // The checklist labels swap based on `mode`, so we expose three booleans either way.
  if (mode.value === 'import') {
    const s = stage.value
    return {
      create: s !== 'idle' && s !== 'submitting',
      upload: s === 'processing' || s === 'done',
      complete: s === 'done',
    }
  }
  return {
    create: stage.value !== 'idle' && stage.value !== 'creating',
    upload: stage.value === 'completing' || stage.value === 'done',
    complete: stage.value === 'done',
  }
})

const checklistItems = computed(() => {
  if (mode.value === 'import') {
    return [
      { label: '1. Submit URL', done: checklistDone.value.create },
      { label: '2. Fetch source', done: checklistDone.value.upload },
      { label: '3. Process + finalize', done: checklistDone.value.complete },
    ]
  }
  return [
    { label: '1. Create record', done: checklistDone.value.create },
    { label: '2. Upload video', done: checklistDone.value.upload },
    { label: '3. Finalize', done: checklistDone.value.complete },
  ]
})

// Polling ceiling — caps total wait at ~5 minutes (60 ticks * 5 s). A clip that hasn't
// reached 'ready' by then is almost certainly stuck (failed fetch, GPU box outage, etc.);
// surface an error and let the user retry rather than spinning forever.
const POLL_INTERVAL_MS = 5_000
const POLL_MAX_TICKS = 60

async function pollImportStatus(clipId: string, tick: number): Promise<void> {
  if (tick > POLL_MAX_TICKS) {
    stage.value = 'error'
    errorMsg.value = 'Import is taking longer than expected. Try again or check back later.'
    return
  }
  try {
    const status = await clips.getStatus(clipId)
    createdShareCode.value = status.shareCode
    if (status.status === 'ready') {
      stage.value = 'done'
      uploadPct.value = 100
      return
    }
    if (status.status === 'failed') {
      stage.value = 'error'
      errorMsg.value = describePipelineFailure(status)
      return
    }
    // Translate server stages → UI stages for the checklist + progress bar.
    if (status.status === 'importing') {
      stage.value = 'importing'
      uploadPct.value = 25
    } else {
      // 'processing' (thumbnail) or 'transcoding' (compress) — both look the same to the user.
      stage.value = 'processing'
      uploadPct.value = 75
    }
    pollTimer = setTimeout(() => void pollImportStatus(clipId, tick + 1), POLL_INTERVAL_MS)
  } catch (err) {
    // 404 right after submit is possible if the row hasn't committed yet — retry once.
    if (err instanceof ApiError && err.status === 404 && tick < 3) {
      pollTimer = setTimeout(() => void pollImportStatus(clipId, tick + 1), POLL_INTERVAL_MS)
      return
    }
    stage.value = 'error'
    errorMsg.value = friendlyUploadError(err)
  }
}

// Maps the structured failureReason code returned by GET /clips/{id}/status into copy a
// user can act on — falls back to a generic message when the worker didn't set a code
// (older clips, unexpected exceptions). Duration is rendered as "Xm Ys" for readability.
function fmtSeconds(s: number): string {
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  const r = s % 60
  return r === 0 ? `${m}m` : `${m}m ${r}s`
}

// Hits POST /clips/import/preview to check duration before the user wastes time in step 2.
// Returns true → caller can advance to step 2. Returns false → previewError is set with
// user-facing copy and the wizard stays on step 1.
async function runImportPreview(): Promise<boolean> {
  if (!isImportUrlValid.value) return false
  // Capture the URL the user clicked Continue on. If they edit the field mid-await, the
  // response that lands belongs to a stale request — applying its title/poster/error to
  // the new URL would be wrong. Compare and bail if it changed.
  const targetUrl = importUrl.value.trim()
  previewLoading.value = true
  previewError.value = null
  previewData.value = null
  try {
    const preview = await clips.previewImport(targetUrl)
    if (importUrl.value.trim() !== targetUrl) return false
    previewData.value = preview
    if (preview.durationSecs != null && preview.durationSecs > preview.maxClipDurationSecs) {
      previewError.value =
        `Clip is ${fmtSeconds(preview.durationSecs)} — max allowed is ${fmtSeconds(preview.maxClipDurationSecs)}. ` +
        'Pick a shorter clip.'
      return false
    }
    if (preview.durationSecs == null) {
      // Surface that we couldn't verify the duration so the user knows the cap may still
      // bite at submit time. Inline note (not blocking) — the worker's ffprobe is the
      // authoritative gate.
      previewError.value =
        `Couldn't read this source's duration up front — max allowed is ${fmtSeconds(preview.maxClipDurationSecs)}. ` +
        "We'll re-check after fetching; longer clips will be rejected at that point."
      // Don't block: Medal.tv / niche extractors sometimes omit duration; we still trust
      // the post-download enforcement.
    }
    // Prefill the title from the extractor unless the user has already typed something.
    if (!title.value.trim() && preview.title) {
      title.value = preview.title.slice(0, 100)
    }
    // Use the platform-resolved thumbnail (works for Medal.tv where the client-side YT
    // fallback can't help). Wins over any URL-derived guess set by the importUrl watcher.
    if (preview.thumbnailUrl) {
      posterUrl.value = preview.thumbnailUrl
    }
    return true
  } catch (err) {
    // Same stale-request guard as the success path — a 4xx that lands after the user has
    // already typed a different URL belongs to the previous probe and must not overwrite
    // the new URL's state.
    if (importUrl.value.trim() !== targetUrl) return false
    if (err instanceof ApiError) {
      const code = (err.body as { code?: string } | null)?.code
      previewError.value =
        code === 'source_unavailable'
          ? 'The source is unavailable — it may be private, geo-blocked, or removed.'
          : code === 'unsupported_host'
            ? 'Only Medal.tv and YouTube URLs are supported right now.'
            : code === 'invalid_url'
              ? 'That URL is not valid.'
              : code === 'import_disabled'
                ? 'URL imports are temporarily disabled.'
                : `Could not read clip metadata (server error ${err.status}).`
    } else {
      previewError.value = 'Could not read clip metadata. Check your connection and try again.'
    }
    return false
  } finally {
    // Only clear loading when we're still the current request. Otherwise a stale response
    // landing AFTER a newer request started would reset the spinner mid-flight.
    if (importUrl.value.trim() === targetUrl) previewLoading.value = false
  }
}

async function continueFromImportStep1() {
  const ok = await runImportPreview()
  if (ok) step.value = 2
}

// Maps the structured failureReason set by the pipeline (any stage — import, thumbnail,
// compress) to user-facing copy. Currently only consumed by the import wizard's polling,
// but the codes cover the full post-submit pipeline so this lives at module scope rather
// than guessing "it was an import error".
function describePipelineFailure(status: ClipStatus): string {
  switch (status.failureReason) {
    case 'source_too_long': {
      const actual = status.durationSecs ? fmtSeconds(status.durationSecs) : 'too long'
      const cap =
        status.maxClipDurationSecs != null ? fmtSeconds(status.maxClipDurationSecs) : 'the limit'
      return `Clip is ${actual} — max allowed is ${cap}. Pick a shorter clip.`
    }
    case 'source_too_large':
      return 'The source file is larger than the upload limit. Pick a shorter or lower-quality clip.'
    case 'source_unavailable':
      return 'The source is unavailable — it may be private, geo-blocked, or removed.'
    case 'fetch_failed':
      return 'The server could not fetch this clip. Double-check the URL or try a different source.'
    case 'thumbnail_failed':
      return "We couldn't generate a thumbnail for this clip. Try again — if it persists, the source may be corrupted."
    case 'transcode_failed':
      return "We couldn't process this clip's video. Try again — if it persists, the source may be in an unsupported format."
    default:
      // No structured code, or one this build doesn't recognise — neutral wording that
      // doesn't pretend to know whether it was an import or a direct upload.
      return 'Processing failed. Please try again.'
  }
}

async function startImport() {
  if (!isImportUrlValid.value) return
  step.value = 3
  stage.value = 'submitting'
  uploadPct.value = 5
  errorMsg.value = null
  try {
    const result = await clips.importFromUrl({
      url: importUrl.value.trim(),
      title: title.value.trim() || null,
      description: desc.value.trim() || null,
      gameId: selectedGame.value?.id ?? null,
      visibility: visibility.value,
      ...(selectedTags.value.length ? { tags: selectedTags.value } : {}),
    })
    createdClipId.value = result.id
    stage.value = 'importing'
    uploadPct.value = 20
    await pollImportStatus(result.id, 0)
  } catch (err) {
    stage.value = 'error'
    errorMsg.value = friendlyImportError(err)
  }
}

function friendlyImportError(err: unknown): string {
  if (err instanceof ApiError) {
    const code = (err.body as { code?: string } | null)?.code
    if (code === 'invalid_url') return 'That URL is not valid.'
    if (code === 'unsupported_host')
      return 'Only Medal.tv and YouTube URLs are supported right now.'
    if (code === 'import_disabled') return 'URL imports are temporarily disabled.'
    return `Server error (${err.status}). Please try again.`
  }
  return friendlyUploadError(err)
}

function goBackToDetails() {
  // Drop the half-created clip id so the next attempt creates a fresh draft instead
  // of re-using the failed one. The orphaned draft row is cleaned up server-side
  // by the future scheduled-sweep job (Phase 2 maintenance).
  if (pollTimer) {
    clearTimeout(pollTimer)
    pollTimer = null
  }
  stage.value = 'idle'
  step.value = 2
  createdClipId.value = null
  createdShareCode.value = null
  uploadPct.value = 0
  errorMsg.value = null
}

const STEPS = [
  { num: '1', label: 'Select file' },
  { num: '2', label: 'Describe' },
  { num: '3', label: 'Upload' },
]
const SOURCES = ['OBS', 'ShadowPlay', 'Medal', 'Xbox', 'PS5', 'Switch']

const inputClass =
  'w-full rounded-md border border-border bg-surface-raised px-3.5 py-3 font-body text-sm text-text-primary outline-none'
const labelClass = 'mb-1.5 block font-mono text-[10px] uppercase tracking-widest text-text-muted'
</script>

<template>
  <main class="mx-auto max-w-225 px-6 pt-8 pb-30">
    <PageHeader title="Upload a clip" class="mb-7">
      <template #caption>
        Any source welcome · OBS, ShadowPlay, Medal, Xbox, consoles — just drop the file
      </template>
    </PageHeader>

    <!-- Stepper -->
    <div class="mb-8 flex overflow-hidden rounded-md border border-border bg-surface-raised">
      <div
        v-for="(s, i) in STEPS"
        :key="s.num"
        :class="[
          'relative flex-1 px-5 py-4 border-b-2',
          i < STEPS.length - 1 ? 'border-r border-r-border' : '',
          step >= Number(s.num) ? 'bg-surface-overlay' : 'bg-transparent',
          step === Number(s.num) ? 'border-b-brand-light' : 'border-b-transparent',
        ]"
      >
        <div
          :class="[
            'mb-1 font-mono text-[10px] uppercase tracking-widest',
            step >= Number(s.num) ? 'text-neon' : 'text-text-muted',
          ]"
        >
          Step {{ s.num }}
        </div>
        <div class="font-heading text-base font-bold uppercase text-text-primary">
          {{ s.label }}
        </div>
      </div>
    </div>

    <!-- Step 1: File picker OR URL import -->
    <div v-if="step === 1">
      <!-- Mode toggle: upload existing file vs. import from a supported URL host. -->
      <div class="mb-5 grid grid-cols-2 gap-2.5">
        <button
          @click="mode = 'upload'"
          :class="[
            'flex cursor-pointer items-center justify-center gap-2 rounded-md border px-4 py-3 font-heading text-sm font-bold uppercase tracking-wider transition-colors',
            mode === 'upload'
              ? 'border-brand-light bg-brand-glow text-text-primary'
              : 'border-border bg-surface-raised text-text-secondary',
          ]"
        >
          <IconUploadCloud :size="16" />
          Upload file
        </button>
        <button
          @click="mode = 'import'"
          :class="[
            'flex cursor-pointer items-center justify-center gap-2 rounded-md border px-4 py-3 font-heading text-sm font-bold uppercase tracking-wider transition-colors',
            mode === 'import'
              ? 'border-brand-light bg-brand-glow text-text-primary'
              : 'border-border bg-surface-raised text-text-secondary',
          ]"
        >
          <IconLink :size="16" />
          Import from URL
        </button>
      </div>

      <!-- File picker (upload mode) -->
      <div v-if="mode === 'upload'">
        <div
          @dragover.prevent="dragging = true"
          @dragleave.prevent="dragging = false"
          @drop.prevent="handleDrop"
          :class="[
            'flex flex-col items-center gap-4 rounded-lg border-2 border-dashed px-6 py-16 text-center transition-[border-color] duration-200',
            dragging ? 'border-brand-light bg-brand-glow' : 'border-border-strong bg-transparent',
          ]"
        >
          <div
            class="flex h-16 w-16 items-center justify-center rounded-full border border-border-strong bg-surface-overlay text-brand-light"
          >
            <IconUploadCloud :size="28" />
          </div>

          <div>
            <div class="mb-1.5 font-heading text-[22px] font-bold uppercase text-text-primary">
              Drop your clip here
            </div>
            <div class="font-body text-sm text-text-secondary">
              MP4 or video — up to {{ MAX_UPLOAD_MB }} MB
            </div>
          </div>

          <label
            class="inline-flex cursor-pointer items-center gap-2 rounded-md bg-brand px-5.5 py-2.5 font-heading text-sm font-bold uppercase tracking-wider text-white"
          >
            <IconFile :size="16" />
            Choose file
            <input type="file" accept="video/*" class="sr-only" @change="handleFileSelect" />
          </label>

          <div class="mt-2 flex flex-wrap justify-center gap-2">
            <span
              v-for="src in SOURCES"
              :key="src"
              class="rounded-sm border border-border bg-surface-overlay px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
            >
              {{ src }}
            </span>
          </div>
        </div>

        <p
          v-if="errorMsg"
          class="mt-4 rounded-md border border-brand bg-surface-overlay px-4 py-2 font-mono text-[12px] text-brand-light"
        >
          {{ errorMsg }}
        </p>

        <div
          v-if="file"
          class="mt-5 flex items-center gap-4 rounded-md border border-neon bg-neon-dim px-5 py-4 text-neon"
        >
          <IconFileText :size="20" class="shrink-0" />
          <div class="min-w-0 flex-1">
            <div
              class="overflow-hidden font-body text-sm whitespace-nowrap text-ellipsis text-text-primary"
            >
              {{ file.name }}
            </div>
            <div class="mt-0.5 font-mono text-[11px] text-text-muted">
              {{ formatSize(file.size) }}
            </div>
          </div>
          <button
            @click="step = 2"
            class="inline-flex shrink-0 cursor-pointer items-center gap-1.5 rounded-md bg-brand-light px-5 py-2.5 font-heading text-sm font-bold whitespace-nowrap uppercase tracking-wider text-white"
          >
            Continue
            <IconArrowRight :size="14" :stroke-width="2.5" />
          </button>
        </div>
      </div>

      <!-- URL input (import mode) -->
      <div v-else>
        <div
          class="flex flex-col gap-4 rounded-lg border-2 border-dashed border-border-strong bg-transparent px-6 py-12 text-center"
        >
          <div
            class="mx-auto flex h-16 w-16 items-center justify-center rounded-full border border-border-strong bg-surface-overlay text-brand-light"
          >
            <IconLink :size="28" />
          </div>
          <div>
            <div class="mb-1.5 font-heading text-[22px] font-bold uppercase text-text-primary">
              Paste a clip URL
            </div>
            <div class="font-body text-sm text-text-secondary">
              Medal.tv or YouTube — we fetch + process it for you
            </div>
          </div>
          <input
            v-model="importUrl"
            type="url"
            placeholder="https://medal.tv/clips/... or https://www.youtube.com/watch?v=..."
            :class="inputClass + ' mx-auto max-w-[28rem]'"
          />
          <div class="mt-1 flex flex-wrap justify-center gap-2">
            <span
              v-for="host in IMPORT_ALLOWED_HOSTS.filter(
                (h) => !h.startsWith('www.') && !h.startsWith('m.'),
              )"
              :key="host"
              class="rounded-sm border border-border bg-surface-overlay px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
            >
              {{ host }}
            </span>
          </div>
        </div>

        <div
          v-if="importUrl && !isImportUrlValid"
          class="mt-4 rounded-md border border-brand bg-surface-overlay px-4 py-2 font-mono text-[12px] text-brand-light"
        >
          Only Medal.tv and YouTube https links are supported right now.
        </div>

        <!-- Preview probe surfacing — either the friendly duration-too-long / unavailable
             error, or a one-line readout so the user can confirm the right clip before
             filling step 2. -->
        <div
          v-if="previewError"
          class="mt-4 rounded-md border border-brand bg-surface-overlay px-4 py-2 font-mono text-[12px] text-brand-light"
        >
          {{ previewError }}
        </div>
        <div
          v-else-if="previewData && previewData.durationSecs != null"
          class="mt-4 rounded-md border border-border bg-surface-overlay px-4 py-2 font-mono text-[12px] text-text-muted"
        >
          <span class="text-neon">{{ fmtSeconds(previewData.durationSecs) }}</span>
          <template v-if="previewData.title"> · {{ previewData.title }}</template>
        </div>

        <div class="mt-5 flex justify-end">
          <button
            :disabled="!isImportUrlValid || previewLoading"
            @click="continueFromImportStep1"
            :class="[
              'inline-flex items-center gap-1.5 rounded-md px-5 py-2.5 font-heading text-sm font-bold uppercase tracking-wider',
              isImportUrlValid && !previewLoading
                ? 'cursor-pointer bg-brand-light text-white'
                : 'cursor-not-allowed border border-border bg-surface-overlay text-text-muted',
            ]"
          >
            {{ previewLoading ? 'Checking…' : 'Continue' }}
            <IconArrowRight v-if="!previewLoading" :size="14" :stroke-width="2.5" />
          </button>
        </div>
      </div>
    </div>

    <!-- Step 2: Metadata -->
    <div v-else-if="step === 2">
      <div class="grid gap-8 grid-cols-1 min-[761px]:grid-cols-[1fr_320px]">
        <div class="flex flex-col gap-6">
          <!-- Game picker -->
          <div>
            <label :class="labelClass"
              >Game <span class="text-[9px] text-text-muted">(optional)</span></label
            >
            <GameSelector v-model="selectedGame" />
          </div>

          <!-- Tags -->
          <div>
            <label :class="labelClass"
              >Tags <span class="text-[9px] text-text-muted">(optional, max 5)</span></label
            >
            <TagInput v-model="selectedTags" :input-class="inputClass" />
          </div>

          <div>
            <div class="mb-1.5 flex items-baseline justify-between">
              <label :class="labelClass + ' mb-0'"
                >Title
                <span v-if="mode === 'import'" class="text-[9px] text-text-muted"
                  >(optional — we'll fill it from the source)</span
                >
              </label>
              <span class="font-mono text-[10px] text-text-muted"> {{ title.length }}/100 </span>
            </div>
            <input
              v-model="title"
              maxlength="100"
              :placeholder="
                mode === 'import'
                  ? 'Override the source title (optional)'
                  : 'What happened in this clip?'
              "
              :class="inputClass"
            />
          </div>

          <div>
            <div class="mb-1.5 flex items-baseline justify-between">
              <label :class="labelClass + ' mb-0'"
                >Description <span class="text-[9px] text-text-muted">(optional)</span></label
              >
              <span class="font-mono text-[10px] text-text-muted"> {{ desc.length }}/500 </span>
            </div>
            <textarea
              v-model="desc"
              maxlength="500"
              rows="4"
              placeholder="Add context, callouts, settings — anything worth knowing"
              :class="inputClass + ' resize-y min-h-24'"
            ></textarea>
          </div>

          <div>
            <label :class="labelClass">Visibility</label>
            <div class="grid grid-cols-2 gap-2.5">
              <button
                v-for="opt in ['public', 'unlisted'] as const"
                :key="opt"
                @click="visibility = opt"
                :class="[
                  'cursor-pointer rounded-md border px-4 py-3.5 text-left transition-all duration-150',
                  visibility === opt
                    ? 'border-brand-light bg-brand-glow text-text-primary'
                    : 'border-border bg-surface-raised text-text-secondary',
                ]"
              >
                <div class="mb-1 flex items-center gap-2">
                  <IconGlobe v-if="opt === 'public'" :size="16" />
                  <IconLink v-else :size="16" />
                  <span class="font-heading text-sm font-bold uppercase">
                    {{ opt === 'public' ? 'Public' : 'Unlisted' }}
                  </span>
                </div>
                <div class="font-body text-xs text-text-muted">
                  {{ opt === 'public' ? 'Visible on feed + search' : 'Only accessible via link' }}
                </div>
              </button>
            </div>
          </div>

          <div class="flex gap-3 pt-2">
            <button
              @click="step = 1"
              class="inline-flex cursor-pointer items-center gap-1.5 rounded-md border border-border bg-surface-overlay px-5 py-3 font-heading text-sm font-bold uppercase text-text-secondary"
            >
              <IconArrowLeft :size="14" :stroke-width="2.5" />
              Back
            </button>
            <button
              :disabled="mode === 'upload' ? !title.trim() : !isImportUrlValid"
              @click="mode === 'upload' ? startUpload() : startImport()"
              :class="[
                'inline-flex flex-1 items-center justify-center gap-2 rounded-md px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-wider transition-all duration-150',
                (mode === 'upload' ? title.trim() : isImportUrlValid)
                  ? 'cursor-pointer border-0 bg-brand-light text-white'
                  : 'cursor-not-allowed border border-border bg-surface-overlay text-text-muted',
              ]"
            >
              {{ mode === 'import' ? 'Start import' : 'Start upload' }}
              <IconArrowRight :size="14" :stroke-width="2.5" />
            </button>
          </div>
        </div>

        <div>
          <label :class="labelClass + ' mb-3'">Preview</label>
          <div class="overflow-hidden rounded-md border border-border bg-surface-raised">
            <div class="relative aspect-video bg-surface-sunken">
              <img
                v-if="posterUrl"
                :src="posterUrl"
                alt="Clip preview"
                class="absolute inset-0 h-full w-full object-cover"
              />
              <div
                v-else
                class="absolute inset-0 flex items-center justify-center font-mono text-[10px] uppercase tracking-widest text-text-muted"
              >
                {{ file?.name ?? 'No file' }}
              </div>
              <div
                class="absolute top-2 right-2 rounded-sm bg-black/60 px-2 py-0.75 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >
                {{ visibility }}
              </div>
            </div>

            <div class="p-3.5">
              <div
                :class="[
                  'mb-2.5 font-heading text-[15px] font-bold leading-[1.3]',
                  title.trim() ? 'not-italic text-text-primary' : 'italic text-text-muted',
                ]"
              >
                {{ title.trim() || 'Your clip title will appear here' }}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Step 3: Upload progress -->
    <div v-else-if="step === 3">
      <div class="mx-auto max-w-140">
        <div
          class="mb-8 flex gap-0 overflow-hidden rounded-md border border-border bg-surface-raised"
        >
          <div class="min-w-0 flex-1 p-4">
            <div class="mb-2 font-heading text-base font-bold leading-[1.3] text-text-primary">
              {{ title || (mode === 'import' ? 'Importing from URL…' : '') }}
            </div>
            <div class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">
              {{ visibility }}
              <template v-if="mode === 'upload' && file"> · {{ formatSize(file.size) }}</template>
              <template v-else-if="mode === 'import'"> · {{ importUrl }}</template>
            </div>
          </div>
        </div>

        <div class="mb-2 flex items-baseline justify-between">
          <span class="font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted">
            {{ progressLabel }}
          </span>
          <span class="font-mono text-[11px] text-neon"> {{ Math.round(uploadPct) }}% </span>
        </div>
        <div class="mb-7 h-1.5 w-full overflow-hidden rounded-full bg-surface-overlay">
          <div
            class="h-full rounded-full bg-[linear-gradient(90deg,var(--color-brand),var(--color-brand-light))] transition-[width] duration-180 ease"
            :style="{ width: uploadPct + '%' }"
          ></div>
        </div>

        <div class="mb-9 flex flex-col gap-3.5">
          <div v-for="item in checklistItems" :key="item.label" class="flex items-center gap-3">
            <div
              :class="[
                'h-2 w-2 shrink-0 rounded-full transition-[background,box-shadow] duration-300',
                item.done ? 'bg-neon shadow-[0_0_8px_var(--color-neon)]' : 'bg-border-strong',
              ]"
            ></div>
            <span
              :class="[
                'font-mono text-xs uppercase tracking-[0.08em] transition-colors duration-300',
                item.done ? 'text-text-primary' : 'text-text-muted',
              ]"
            >
              {{ item.label }}
            </span>
          </div>
        </div>

        <div
          v-if="stage === 'error'"
          class="mb-6 rounded-md border border-brand bg-surface-overlay px-4 py-3 font-mono text-[12px] text-brand-light"
        >
          {{ errorMsg }}
          <button
            class="mt-2 block cursor-pointer rounded-sm border border-border bg-surface-raised px-3 py-1.5 text-text-primary"
            @click="goBackToDetails"
          >
            Back to details
          </button>
        </div>

        <div v-if="stage === 'done'" class="flex flex-col gap-2.5">
          <button
            :disabled="!createdClipId"
            @click="createdClipId && router.push(`/clip/${createdClipId}`)"
            class="flex w-full cursor-pointer items-center justify-center gap-2 rounded-md bg-brand-light px-6 py-3.5 font-heading text-base font-bold uppercase tracking-wider text-white disabled:cursor-not-allowed disabled:opacity-50"
          >
            View your clip
            <IconArrowRight :size="16" :stroke-width="2.5" />
          </button>
          <button
            @click="router.push('/')"
            class="flex w-full cursor-pointer items-center justify-center rounded-md border border-border bg-transparent px-6 py-3 font-heading text-sm font-bold uppercase tracking-wider text-text-secondary"
          >
            Back to feed
          </button>
        </div>
      </div>
    </div>
  </main>
</template>
