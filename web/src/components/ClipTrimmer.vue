<script setup lang="ts">
import { ref, computed, watch, onUnmounted } from 'vue'
import IconPlay from '@/components/icons/IconPlay.vue'

// Rewynd-style pre-upload trimmer: video preview + filmstrip with two draggable
// handles. Emits null while the range covers the whole clip, so the caller only
// sends a trim when the user actually cut something.
export interface TrimRange {
  start: number
  end: number
}

const props = defineProps<{ file: File }>()
const model = defineModel<TrimRange | null>({ default: null })

// Matches the server's minimum trimmed span (ClipUploadService.MinTrimSpanSecs).
const MIN_GAP = 0.2
// Handle grab distance in px; presses further away seek instead.
const GRAB = 12
const FRAME_COUNT = 12
// Same "did a handle actually move" threshold rewynd uses.
const CHANGED_EPS = 0.05

const videoEl = ref<HTMLVideoElement | null>(null)
const barEl = ref<HTMLElement | null>(null)

const objectUrl = ref('')
const duration = ref(0)
const start = ref(0)
const end = ref(0)
const playhead = ref(0)
const playing = ref(false)
const frames = ref<string[]>([])
const decodeFailed = ref(false)

let frameRequestId = 0

watch(
  () => props.file,
  (f) => {
    if (objectUrl.value) URL.revokeObjectURL(objectUrl.value)
    objectUrl.value = URL.createObjectURL(f)
    duration.value = 0
    start.value = 0
    end.value = 0
    playhead.value = 0
    playing.value = false
    frames.value = []
    decodeFailed.value = false
    model.value = null
    frameRequestId++
  },
  { immediate: true },
)

onUnmounted(() => {
  if (objectUrl.value) URL.revokeObjectURL(objectUrl.value)
  frameRequestId++
})

const changed = computed(
  () => start.value > CHANGED_EPS || end.value < duration.value - CHANGED_EPS,
)

watch([start, end, duration], () => {
  model.value = duration.value > 0 && changed.value ? { start: start.value, end: end.value } : null
})

function onLoadedMetadata() {
  const v = videoEl.value
  if (!v || !Number.isFinite(v.duration) || v.duration <= 0) return
  duration.value = v.duration
  end.value = v.duration
  void captureFrames(++frameRequestId)
}

function onDecodeError() {
  decodeFailed.value = true
  model.value = null
}

// Filmstrip frames via an offscreen <video> + <canvas>, sequential seeks. Best-effort:
// a failed capture leaves a blank strip, trimming still works.
async function captureFrames(requestId: number): Promise<void> {
  const src = objectUrl.value
  const dur = duration.value
  if (!src || dur <= 0) return
  const video = document.createElement('video')
  video.muted = true
  video.preload = 'auto'
  video.src = src
  try {
    await new Promise<void>((resolve, reject) => {
      video.onloadeddata = () => resolve()
      video.onerror = () => reject(new Error('decode failed'))
    })
    if (requestId !== frameRequestId) return
    const height = 96
    const scale = height / (video.videoHeight || height)
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round((video.videoWidth || height) * scale))
    canvas.height = height
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    const captured: string[] = []
    for (let i = 0; i < FRAME_COUNT; i++) {
      if (requestId !== frameRequestId) return
      const t = ((i + 0.5) / FRAME_COUNT) * dur
      await new Promise<void>((resolve, reject) => {
        video.onseeked = () => resolve()
        video.onerror = () => reject(new Error('seek failed'))
        video.currentTime = t
      })
      ctx.drawImage(video, 0, 0, canvas.width, canvas.height)
      captured.push(canvas.toDataURL('image/jpeg', 0.6))
    }
    if (requestId === frameRequestId) frames.value = captured
  } catch {
    // blank strip fallback
  } finally {
    video.removeAttribute('src')
    video.load()
  }
}

// --- playback ------------------------------------------------------------

function togglePlay() {
  const v = videoEl.value
  if (!v || duration.value <= 0) return
  if (playing.value) {
    v.pause()
    return
  }
  // Resume inside the kept range; from its start when the playhead sits outside.
  if (playhead.value < start.value || playhead.value >= end.value - 0.1) {
    v.currentTime = start.value
  }
  void v.play()
}

function onTimeUpdate() {
  const v = videoEl.value
  if (!v) return
  playhead.value = v.currentTime
  if (playing.value && v.currentTime >= end.value) {
    v.pause()
    v.currentTime = start.value
    playhead.value = start.value
  }
}

function seekTo(t: number) {
  const v = videoEl.value
  const clamped = Math.min(Math.max(t, 0), duration.value)
  playhead.value = clamped
  if (v) v.currentTime = clamped
}

// --- trim bar interaction ------------------------------------------------

type Handle = 'start' | 'end' | 'seek'
let dragging: Handle | null = null

function timeAtX(clientX: number): number {
  const rect = barEl.value!.getBoundingClientRect()
  const frac = Math.min(Math.max((clientX - rect.left) / rect.width, 0), 1)
  return frac * duration.value
}

function pixelOf(t: number): number {
  const rect = barEl.value!.getBoundingClientRect()
  return rect.left + (t / duration.value) * rect.width
}

function onPointerDown(e: PointerEvent) {
  if (duration.value <= 0 || !barEl.value) return
  barEl.value.focus()
  const distStart = Math.abs(e.clientX - pixelOf(start.value))
  const distEnd = Math.abs(e.clientX - pixelOf(end.value))
  if (distStart <= GRAB && distStart <= distEnd) dragging = 'start'
  else if (distEnd <= GRAB) dragging = 'end'
  else dragging = 'seek'
  barEl.value.setPointerCapture(e.pointerId)
  applyDrag(timeAtX(e.clientX))
}

function onPointerMove(e: PointerEvent) {
  if (dragging) applyDrag(timeAtX(e.clientX))
}

function onPointerUp() {
  dragging = null
}

function applyDrag(t: number) {
  if (dragging === 'start') {
    start.value = Math.min(Math.max(t, 0), end.value - MIN_GAP)
    seekTo(start.value)
  } else if (dragging === 'end') {
    end.value = Math.max(Math.min(t, duration.value), start.value + MIN_GAP)
    seekTo(end.value)
  } else if (dragging === 'seek') {
    seekTo(t)
  }
}

function onKeyDown(e: KeyboardEvent) {
  if (duration.value <= 0) return
  const step = e.shiftKey ? 1 : 0.1
  switch (e.key) {
    case 'ArrowLeft':
      seekTo(playhead.value - step)
      break
    case 'ArrowRight':
      seekTo(playhead.value + step)
      break
    case 'i':
    case 'I':
      start.value = Math.min(playhead.value, end.value - MIN_GAP)
      break
    case 'o':
    case 'O':
      end.value = Math.max(playhead.value, start.value + MIN_GAP)
      break
    case 'Home':
      seekTo(start.value)
      break
    case 'End':
      seekTo(end.value)
      break
    case ' ':
      togglePlay()
      break
    case 'Escape':
      reset()
      barEl.value?.blur()
      break
    default:
      return
  }
  e.preventDefault()
}

function reset() {
  start.value = 0
  end.value = duration.value
}

function fmtClock(secs: number): string {
  // Round before splitting so 119.96s renders 2:00.0, not 1:60.0.
  const rounded = Math.round(secs * 10) / 10
  const m = Math.floor(rounded / 60)
  const s = rounded - m * 60
  return `${m}:${s < 10 ? '0' : ''}${s.toFixed(1)}`
}

const startPct = computed(() => (duration.value > 0 ? (start.value / duration.value) * 100 : 0))
const endPct = computed(() => (duration.value > 0 ? (end.value / duration.value) * 100 : 100))
const playheadPct = computed(() =>
  duration.value > 0 ? (playhead.value / duration.value) * 100 : 0,
)

const kbdClass =
  'rounded-sm border border-border px-1 py-px text-[9px] font-semibold text-text-secondary'
</script>

<template>
  <div class="flex flex-col gap-3">
    <div class="relative aspect-video overflow-hidden rounded-lg border border-border bg-black">
      <video
        ref="videoEl"
        :src="objectUrl"
        playsinline
        class="h-full w-full object-contain"
        @loadedmetadata="onLoadedMetadata"
        @error="onDecodeError"
        @timeupdate="onTimeUpdate"
        @play="playing = true"
        @pause="playing = false"
        @click="togglePlay"
      ></video>
      <button
        v-if="!playing && !decodeFailed"
        type="button"
        aria-label="Play preview"
        class="absolute inset-0 m-auto flex size-14 cursor-pointer items-center justify-center rounded-full border border-white/30 bg-black/55 text-[#f4f1e8] transition-colors duration-150 hover:border-white/60"
        @click="togglePlay"
      >
        <IconPlay :size="22" />
      </button>
      <div
        v-if="decodeFailed"
        class="absolute inset-0 flex items-center justify-center px-6 text-center text-[11px] text-[#f4f1e8]/80"
      >
        This browser can't preview this file — it will upload untrimmed.
      </div>
    </div>

    <div v-if="!decodeFailed" class="flex flex-col gap-2">
      <div
        ref="barEl"
        tabindex="0"
        role="slider"
        aria-label="Trim range"
        :aria-valuemin="0"
        :aria-valuemax="duration"
        :aria-valuenow="playhead"
        :aria-valuetext="`Playhead ${fmtClock(playhead)}, keeping ${fmtClock(start)} to ${fmtClock(end)}`"
        class="relative h-14 cursor-pointer touch-none overflow-hidden rounded-lg border border-border bg-surface-high outline-none select-none focus:border-accent"
        @pointerdown="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointercancel="onPointerUp"
        @keydown="onKeyDown"
      >
        <div v-if="frames.length" class="absolute inset-0 flex">
          <img
            v-for="(frame, i) in frames"
            :key="i"
            :src="frame"
            alt=""
            draggable="false"
            class="h-full min-w-0 flex-1 object-cover"
          />
        </div>
        <!-- scrims outside the kept range -->
        <div class="absolute inset-y-0 left-0 bg-black/70" :style="{ width: startPct + '%' }"></div>
        <div
          class="absolute inset-y-0 right-0 bg-black/70"
          :style="{ width: 100 - endPct + '%' }"
        ></div>
        <!-- kept-range outline -->
        <div
          class="pointer-events-none absolute inset-y-0 border-y-2 border-accent"
          :style="{ left: startPct + '%', width: endPct - startPct + '%' }"
        ></div>
        <!-- playhead -->
        <div
          v-if="playhead > start + 0.01 && playhead < end - 0.01"
          class="pointer-events-none absolute inset-y-0 w-px bg-white/80"
          :style="{ left: playheadPct + '%' }"
        ></div>
        <!-- handles -->
        <div
          class="pointer-events-none absolute inset-y-0 w-1 -translate-x-1/2 rounded-full bg-accent"
          :style="{ left: startPct + '%' }"
        ></div>
        <div
          class="pointer-events-none absolute inset-y-0 w-1 -translate-x-1/2 rounded-full bg-accent"
          :style="{ left: endPct + '%' }"
        ></div>
      </div>

      <div class="flex flex-wrap items-center justify-between gap-2">
        <div class="flex flex-wrap items-center gap-1.5 text-[10px] text-text-muted">
          Click the timeline, then
          <span :class="kbdClass">←</span><span :class="kbdClass">→</span> seek
          <span :class="kbdClass">i</span><span :class="kbdClass">o</span> trim in/out
          <span :class="kbdClass">space</span> play <span :class="kbdClass">esc</span> reset
        </div>
        <div class="flex items-center gap-3 text-[11px] text-text-secondary">
          <span
            >Start <span class="font-semibold text-text-primary">{{ fmtClock(start) }}</span></span
          >
          <span
            >End <span class="font-semibold text-text-primary">{{ fmtClock(end) }}</span></span
          >
          <span>
            Length
            <span class="font-semibold" :class="changed ? 'text-accent' : 'text-text-primary'">
              {{ fmtClock(Math.max(end - start, 0)) }}
            </span>
          </span>
          <button
            v-if="changed"
            type="button"
            class="cursor-pointer rounded-lg border border-border-strong px-2 py-0.5 text-[11px] font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
            @click="reset"
          >
            Reset
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
