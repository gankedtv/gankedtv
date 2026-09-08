<script setup lang="ts">
import { ref, computed, watch, onUnmounted } from 'vue'
import { clips } from '@/api/clips'
import {
  CROP_HANDLES,
  CROP_RATIOS,
  FULL_FRAME,
  clampRect,
  defaultSeedRect,
  handlePosition,
  hitTestHandle,
  isCropChanged,
  isFarFromWidescreen,
  maxRectForRatio,
  moveRect,
  outputSize,
  rectRatioFor,
  resizeRect,
  seedCropRect,
  toCropModel,
  type CropHandle,
  type CropRect,
  type CropRatioKey,
} from '@/lib/crop'

// Drag-a-rectangle cropper over a video preview. Emits null while the rect covers the whole
// frame — or while the "crop this clip" toggle is off — so the caller only sends a crop when
// the user actually asked for one. All the clamping and ratio math lives in lib/crop.
//
// Deliberately NOT an extended ClipTrimmer: the two share only the shape of the pointer engine,
// and the trimmer's filmstrip capture pipeline is dead weight here.
//
// Source is either a local File (pre-upload wizard) or the URL of an already-published master
// (post-publish re-crop). Exactly one is expected; `file` wins if both are passed. `clipId`
// enables the "Remove black bars" suggestion, which needs a server-side clip to probe.

const props = defineProps<{ file?: File; src?: string; clipId?: string }>()
const model = defineModel<CropRect | null>({ default: null })

// Handle grab radius in px, measured in screen space so it feels the same on any frame ratio.
const GRAB = 12
// Keyboard nudge, as a fraction of the frame.
const STEP = 0.01
// The preview can't grow past this or a 9:16 source would run off the page. Capping WIDTH, not
// height: `aspect-ratio` + `max-height` doesn't shrink width, so a height cap silently breaks
// the ratio instead of constraining the box.
const MAX_PREVIEW_WIDTH = 640

const videoEl = ref<HTMLVideoElement | null>(null)
const boxEl = ref<HTMLElement | null>(null)

const objectUrl = ref('')
const frameWidth = ref(0)
const frameHeight = ref(0)
const rect = ref<CropRect>({ ...FULL_FRAME })
// Whether the rect on screen is actually sent. Mounting this component is NOT consent to
// destroy a quarter of someone's frame: the seed below pre-frames an ultrawide capture to 16:9,
// and without this gate opening the tab to look at it — then leaving — would silently publish
// the cut. It opens ticked whenever the seed armed a crop, so the ultrawide case still needs no
// discovery; it's a stated, visible choice the user can untick rather than a side effect.
const applyCrop = ref(false)
// The cropper OPENS on this preset and seeds the rect to it, so an ultrawide capture arrives
// already framed to 16:9 with the bars outside the rect — the overwhelmingly common intent, and
// it doesn't depend on the user discovering the pills. On a source that's already 16:9 the
// preset rect is the whole frame, so the model stays null and nothing is sent.
const DEFAULT_RATIO: CropRatioKey = '16:9'
const ratioKey = ref<CropRatioKey>(DEFAULT_RATIO)
const decodeFailed = ref(false)
const dragging = ref<CropHandle | null>(null)
// Only announced on keyup and drag-end — a live region firing at pointer rate is unusable.
const announcement = ref('')

// Suggestion state. `previous` doubles as the Undo buffer so applying is always reversible.
const suggesting = ref(false)
const suggestionMissed = ref(false)
const undoBuffer = ref<CropRect | null>(null)

const mediaSrc = computed(() => (props.file ? objectUrl.value : (props.src ?? '')))
const loaded = computed(() => frameWidth.value > 0 && frameHeight.value > 0)
const frameRatio = computed(() => (loaded.value ? frameWidth.value / frameHeight.value : 16 / 9))
const rectRatio = computed(() => rectRatioFor(ratioKey.value, frameRatio.value))

// Resets internal state only — the parent owns clearing the model on a new pick, so a remount
// (navigating back to the crop tab) restores the earlier rect via the seed in onLoadedMetadata.
watch(
  () => props.file ?? props.src,
  () => {
    if (objectUrl.value) {
      URL.revokeObjectURL(objectUrl.value)
      objectUrl.value = ''
    }
    if (props.file) objectUrl.value = URL.createObjectURL(props.file)
    frameWidth.value = 0
    frameHeight.value = 0
    rect.value = { ...FULL_FRAME }
    applyCrop.value = false
    decodeFailed.value = false
    suggestionMissed.value = false
    undoBuffer.value = null
  },
  { immediate: true },
)

onUnmounted(() => {
  if (objectUrl.value) URL.revokeObjectURL(objectUrl.value)
})

watch(
  [rect, applyCrop],
  ([r, apply]) => {
    model.value = apply ? toCropModel(r) : null
  },
  { deep: true },
)

function onLoadedMetadata() {
  const v = videoEl.value
  if (!v || !v.videoWidth || !v.videoHeight) {
    onDecodeError()
    return
  }
  frameWidth.value = v.videoWidth
  frameHeight.value = v.videoHeight
  decodeFailed.value = false

  const restored = model.value
  // frameRatio is live now that the dimensions are in, so the default resolves against THIS
  // source rather than the 16/9 placeholder the computed falls back to before load.
  const seeded = seedCropRect(
    restored,
    defaultSeedRect(frameWidth.value / frameHeight.value, DEFAULT_RATIO),
  )
  rect.value = seeded
  // Ticked for a rect the user already committed to, and for a seed that found bars to remove;
  // a source with nothing to crop opens on the full frame, where the toggle would be a no-op.
  applyCrop.value = !!restored || isCropChanged(seeded)
  // A restored crop dictates its own preset. A fresh open keeps DEFAULT_RATIO when the seed
  // actually armed a crop; when defaultSeedRect declined (portrait, 4:3, already-16:9) the rect
  // is the full frame, so infer instead — leaving the pill on 16:9 would claim a lock the rect
  // doesn't have and would snap the frame away on the user's first drag.
  ratioKey.value = restored || isCropChanged(seeded) ? matchRatioKey(seeded) : 'free'
}

function onDecodeError() {
  decodeFailed.value = true
  // Without decoded dimensions the overlay would be misaligned against the rendered video rect,
  // so hide it and make sure no crop rides along from a stale model.
  model.value = null
}

// Nearest preset whose rect ratio the restored crop already satisfies; 'free' when none does.
function matchRatioKey(r: CropRect): CropRatioKey {
  const current = r.width / r.height
  for (const option of CROP_RATIOS) {
    if (option.key === 'free') continue
    const target = rectRatioFor(option.key, frameRatio.value)
    if (target !== null && Math.abs(current - target) < 0.01) return option.key
  }
  return 'free'
}

// --- preview box sizing --------------------------------------------------

// Size the box to the SOURCE ratio rather than a fixed aspect-video + object-contain. With
// object-contain the rendered video rect ≠ the container rect for a 21:9 source, so an inset-0
// overlay would sit over the letterbox bars instead of over the picture.
const boxStyle = computed(() => {
  if (!loaded.value) return {}
  return {
    aspectRatio: `${frameWidth.value} / ${frameHeight.value}`,
    maxWidth: `${MAX_PREVIEW_WIDTH}px`,
  }
})

// --- pointer interaction -------------------------------------------------

let lastPointer: { x: number; y: number } | null = null

function pointerFraction(e: PointerEvent): { x: number; y: number } {
  const box = boxEl.value!.getBoundingClientRect()
  return {
    x: (e.clientX - box.left) / box.width,
    y: (e.clientY - box.top) / box.height,
  }
}

function onPointerDown(e: PointerEvent) {
  if (!loaded.value || decodeFailed.value || !boxEl.value) return
  boxEl.value.focus()
  const p = pointerFraction(e)
  const box = boxEl.value.getBoundingClientRect()
  dragging.value = hitTestHandle(rect.value, p, box, GRAB) ?? 'move'
  lastPointer = p
  boxEl.value.setPointerCapture(e.pointerId)
  e.preventDefault()
}

function onPointerMove(e: PointerEvent) {
  if (!dragging.value || !boxEl.value) return
  const p = pointerFraction(e)
  if (dragging.value === 'move') {
    // Delta-based: an absolute move would teleport the rect to the pointer on the first press.
    if (lastPointer) rect.value = moveRect(rect.value, p.x - lastPointer.x, p.y - lastPointer.y)
  } else {
    rect.value = resizeRect(rect.value, dragging.value, p, rectRatio.value)
  }
  // Dragging the rect IS the ask, so nobody has to find the toggle to make a crop they framed
  // by hand actually happen.
  applyCrop.value = true
  lastPointer = p
}

function onPointerUp() {
  if (!dragging.value) return
  dragging.value = null
  lastPointer = null
  announce()
}

// --- ratio presets -------------------------------------------------------

function pickRatio(key: CropRatioKey) {
  ratioKey.value = key
  const target = rectRatioFor(key, frameRatio.value)
  // 'free' keeps whatever is on screen; every other preset re-frames to the largest rect that
  // satisfies it, which is the only unambiguous answer when the current rect doesn't fit.
  rect.value = target === null ? rect.value : maxRectForRatio(target)
  applyCrop.value = true
  undoBuffer.value = null
  announce()
}

// --- auto-suggest --------------------------------------------------------

async function suggest() {
  if (!props.clipId || suggesting.value) return
  suggesting.value = true
  suggestionMissed.value = false
  try {
    const result = await clips.cropSuggestion(props.clipId)
    if (!result.detected || !result.crop) {
      suggestionMissed.value = true
      return
    }
    // Never applied silently, and always reversible — the detector is a guess, and a wrong one
    // that the user can't back out of would cost them a re-encode.
    undoBuffer.value = { ...rect.value }
    rect.value = clampRect(result.crop)
    applyCrop.value = true
    ratioKey.value = matchRatioKey(rect.value)
    announce()
  } catch {
    suggestionMissed.value = true
  } finally {
    suggesting.value = false
  }
}

function undoSuggestion() {
  if (!undoBuffer.value) return
  rect.value = undoBuffer.value
  ratioKey.value = matchRatioKey(rect.value)
  undoBuffer.value = null
  announce()
}

// --- keyboard ------------------------------------------------------------

function onKeyDown(e: KeyboardEvent) {
  if (!loaded.value || decodeFailed.value) return
  const shift = e.shiftKey
  switch (e.key) {
    case 'ArrowLeft':
      rect.value = shift ? resizeBy(-STEP, 0) : moveRect(rect.value, -STEP, 0)
      break
    case 'ArrowRight':
      rect.value = shift ? resizeBy(STEP, 0) : moveRect(rect.value, STEP, 0)
      break
    case 'ArrowUp':
      rect.value = shift ? resizeBy(0, -STEP) : moveRect(rect.value, 0, -STEP)
      break
    case 'ArrowDown':
      rect.value = shift ? resizeBy(0, STEP) : moveRect(rect.value, 0, STEP)
      break
    case 'Escape':
      reset()
      break
    default:
      return
  }
  // Nudging the rect is consent to apply it; Escape is the opposite ask, and reset() has just
  // cleared the toggle for exactly that reason.
  if (e.key !== 'Escape') applyCrop.value = true
  // Every handled branch preventDefaults. ClipCropDialog checks !e.defaultPrevented before
  // treating Escape as "close the dialog", so an unmarked Escape here would close the editor
  // instead of resetting the crop.
  e.preventDefault()
}

function resizeBy(dx: number, dy: number): CropRect {
  const target = {
    x: rect.value.x + rect.value.width + dx,
    y: rect.value.y + rect.value.height + dy,
  }
  return resizeRect(rect.value, 'se', target, rectRatio.value)
}

function reset() {
  // Rect only — the pill describes the lock for the NEXT drag, and silently switching it to
  // 'free' would change how the editor behaves afterwards without the user asking.
  rect.value = { ...FULL_FRAME }
  applyCrop.value = false
  undoBuffer.value = null
  announce()
}

// --- readouts ------------------------------------------------------------

// Read off the rect rather than the model: the model is written by a watcher, so during a drag
// it lags a tick behind and the readout would flicker.
const changed = computed(() => isCropChanged(rect.value))

const output = computed(() =>
  loaded.value ? outputSize(rect.value, frameWidth.value, frameHeight.value) : null,
)

const farFromWidescreen = computed(
  () =>
    loaded.value &&
    changed.value &&
    applyCrop.value &&
    isFarFromWidescreen(rect.value, frameRatio.value),
)

// Only offered once there is a crop to apply: on the full frame it would toggle nothing.
const showApplyToggle = computed(() => loaded.value && !decodeFailed.value && changed.value)

function announce() {
  const o = output.value
  announcement.value = o
    ? `Crop ${o.width} by ${o.height} pixels, ${Math.round(rect.value.width * 100)} percent wide, ${Math.round(rect.value.height * 100)} percent tall`
    : ''
}

// Percentage geometry for the overlay layers, driven by the same rect the math works in.
const pct = computed(() => ({
  left: `${rect.value.x * 100}%`,
  top: `${rect.value.y * 100}%`,
  width: `${rect.value.width * 100}%`,
  height: `${rect.value.height * 100}%`,
  right: `${(1 - rect.value.x - rect.value.width) * 100}%`,
  bottom: `${(1 - rect.value.y - rect.value.height) * 100}%`,
}))

const HANDLE_CURSOR: Record<string, string> = {
  nw: 'cursor-nwse-resize',
  n: 'cursor-ns-resize',
  ne: 'cursor-nesw-resize',
  e: 'cursor-ew-resize',
  se: 'cursor-nwse-resize',
  s: 'cursor-ns-resize',
  sw: 'cursor-nesw-resize',
  w: 'cursor-ew-resize',
}

function handleStyle(handle: CropHandle) {
  const p = handlePosition(handle, rect.value)
  return { left: `${p.x * 100}%`, top: `${p.y * 100}%` }
}

const kbdClass =
  'rounded-sm border border-border px-1 py-px text-[9px] font-semibold text-text-secondary'

const decodeFailedMessage = computed(() =>
  props.file
    ? "This browser can't preview this file. It will upload uncropped."
    : "This browser can't play this clip, so it can't be cropped here.",
)
</script>

<template>
  <div class="flex flex-col gap-3">
    <!-- Ratio presets -->
    <div v-if="!decodeFailed" class="flex flex-wrap items-center gap-1.5">
      <button
        v-for="option in CROP_RATIOS"
        :key="option.key"
        type="button"
        :disabled="!loaded"
        :aria-pressed="ratioKey === option.key"
        :class="[
          'rounded-full border px-3 py-1 text-[11px] font-semibold transition-colors duration-150',
          !loaded
            ? 'cursor-not-allowed border-border text-text-muted opacity-40'
            : ratioKey === option.key
              ? 'cursor-pointer border-accent-border bg-accent-bg text-accent'
              : 'cursor-pointer border-border text-text-muted hover:border-accent-border hover:text-accent',
        ]"
        @click="pickRatio(option.key)"
      >
        {{ option.label }}
      </button>

      <div class="ml-auto flex items-center gap-2">
        <button
          v-if="clipId && !suggestionMissed"
          type="button"
          :disabled="!loaded || suggesting"
          class="cursor-pointer rounded-lg border border-border-strong px-3 py-1 text-[11px] font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-50"
          @click="suggest"
        >
          {{ suggesting ? 'Checking…' : 'Remove black bars' }}
        </button>
        <button
          v-if="undoBuffer"
          type="button"
          class="cursor-pointer rounded-lg border border-border-strong px-3 py-1 text-[11px] font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
          @click="undoSuggestion"
        >
          Undo
        </button>
      </div>
    </div>

    <!-- The consent gate. Opening this editor pre-frames an ultrawide capture to 16:9, which is
         almost always what the user wants — but the cut is permanent, so it has to be something
         they can see and untick rather than something that happens because a tab was opened. -->
    <label
      v-if="showApplyToggle"
      :class="[
        'flex cursor-pointer items-start gap-2.5 rounded-lg border px-3 py-2.5 transition-colors duration-150',
        applyCrop ? 'border-accent-border bg-accent-bg' : 'border-border',
      ]"
    >
      <input v-model="applyCrop" type="checkbox" class="mt-0.5 size-3.5 shrink-0 accent-accent" />
      <span class="text-[11px] leading-relaxed text-text-muted">
        <span class="font-semibold text-text-primary">Crop this clip</span
        ><template v-if="output"> to {{ output.width }} × {{ output.height }}</template
        >. Everything outside the box is removed permanently. Untick to publish the full frame.
      </span>
    </label>

    <!-- Preview + crop overlay. The box is sized to the SOURCE ratio so the overlay lines up
         with the picture rather than with letterbox bars. -->
    <div class="flex justify-center">
      <div
        ref="boxEl"
        tabindex="0"
        role="group"
        aria-label="Crop region"
        :aria-describedby="`crop-readout-${clipId ?? 'local'}`"
        :style="loaded ? boxStyle : undefined"
        :class="[
          'relative w-full touch-none overflow-hidden rounded-lg border border-border bg-black outline-none select-none focus:border-accent',
          loaded ? '' : 'aspect-video',
          dragging === 'move' ? 'cursor-grabbing' : 'cursor-crosshair',
        ]"
        @pointerdown="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointercancel="onPointerUp"
        @keydown="onKeyDown"
        @keyup="announce"
      >
        <video
          ref="videoEl"
          :src="mediaSrc"
          playsinline
          muted
          class="absolute inset-0 h-full w-full"
          @loadedmetadata="onLoadedMetadata"
          @error="onDecodeError"
        ></video>

        <div
          v-if="decodeFailed"
          class="absolute inset-0 flex items-center justify-center px-6 text-center text-[11px] text-[#f4f1e8]/80"
        >
          {{ decodeFailedMessage }}
        </div>

        <template v-else-if="loaded">
          <!-- Scrims outside the kept rect -->
          <div
            class="pointer-events-none absolute inset-x-0 top-0 bg-black/70"
            :style="{ height: pct.top }"
          ></div>
          <div
            class="pointer-events-none absolute inset-x-0 bottom-0 bg-black/70"
            :style="{ height: pct.bottom }"
          ></div>
          <div
            class="pointer-events-none absolute left-0 bg-black/70"
            :style="{ top: pct.top, height: pct.height, width: pct.left }"
          ></div>
          <div
            class="pointer-events-none absolute right-0 bg-black/70"
            :style="{ top: pct.top, height: pct.height, width: pct.right }"
          ></div>

          <!-- Kept-rect outline -->
          <div
            class="pointer-events-none absolute border border-accent"
            :style="{ left: pct.left, top: pct.top, width: pct.width, height: pct.height }"
          ></div>

          <!-- Mint grip bars on the 8 handles. The wrapper stays interactive purely so the
               resize cursor shows on hover — the press still bubbles to the container, which
               owns all the pointer logic and re-resolves the handle by hit-test. Same split as
               ClipTrimmer's handle wrappers. -->
          <div
            v-for="handle in CROP_HANDLES"
            :key="handle"
            :class="['absolute size-4 -translate-x-1/2 -translate-y-1/2', HANDLE_CURSOR[handle]]"
            :style="handleStyle(handle)"
          >
            <div
              class="pointer-events-none absolute top-1/2 left-1/2 size-2.5 -translate-x-1/2 -translate-y-1/2 rounded-sm bg-accent"
            ></div>
          </div>
        </template>
      </div>
    </div>

    <div v-if="!decodeFailed" class="flex flex-col gap-2">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <div class="flex flex-wrap items-center gap-1.5 text-[10px] text-text-muted">
          Click the frame, then <span :class="kbdClass">↑</span><span :class="kbdClass">↓</span>
          <span :class="kbdClass">←</span><span :class="kbdClass">→</span> move
          <span :class="kbdClass">shift</span> + arrows resize
          <span :class="kbdClass">esc</span> reset
        </div>
        <div class="flex items-center gap-3 text-[11px] text-text-secondary">
          <span :id="`crop-readout-${clipId ?? 'local'}`" aria-live="polite" class="sr-only">{{
            announcement
          }}</span>
          <span v-if="output">
            Output
            <span
              class="font-semibold"
              :class="changed && applyCrop ? 'text-accent' : 'text-text-primary'"
            >
              {{ output.width }}×{{ output.height }}
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

      <p v-if="suggestionMissed" class="m-0 text-[10px] text-text-muted">
        We couldn't find black bars to remove. Drag the edges to crop manually.
      </p>

      <!-- Feed cards stay aspect-video object-cover, so anything far from 16:9 centre-crops
           there. Disclosed rather than hidden. -->
      <p v-if="farFromWidescreen" class="m-0 text-[10px] text-text-muted">
        This crop isn't close to 16:9, so feed thumbnails will show a centred slice of it. The clip
        page plays the full crop.
      </p>
    </div>
  </div>
</template>
