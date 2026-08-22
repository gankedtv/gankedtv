// Pure crop-rect logic for the cropper, split out of ClipCropper.vue so the regression-prone
// parts (ratio math against a non-16:9 frame, clamping, null-when-full-frame model semantics)
// are unit-testable without a media-capable DOM. Mirrors the lib/trim.ts split.

// A crop rect in NORMALIZED 0..1 fractions of the source frame — the same space the server's
// /complete and /edit bodies use, so a rect goes over the wire without conversion. Never pixels:
// the master is rescaled by the server's height cap on every edit generation, so a pixel rect
// would mean something different after each one.
export interface CropRect {
  x: number
  y: number
  width: number
  height: number
}

// Matches the server's ClipCropValidation.MinCropExtent.
export const MIN_CROP_EXTENT = 0.05
// "Did the user actually crop something" threshold. A drag of less than half a percent on every
// edge is almost certainly a mis-click, and sending it would cost a re-encode for no visible
// change.
export const CROP_CHANGED_EPS = 0.005

export type CropRatioKey = 'original' | '16:9' | '21:9' | '4:3' | '9:16' | 'free'

export interface CropRatioOption {
  key: CropRatioKey
  label: string
}

export const CROP_RATIOS: CropRatioOption[] = [
  { key: 'original', label: 'Original' },
  { key: '16:9', label: '16:9' },
  { key: '21:9', label: '21:9' },
  { key: '4:3', label: '4:3' },
  { key: '9:16', label: '9:16' },
  { key: 'free', label: 'Free' },
]

// Target output aspect ratio (width/height) for a preset, or null when the rect is unconstrained.
// 'original' is null too — locking to the frame's own ratio is what the *rect* ratio of 1 means,
// handled by ratioForKey's caller.
export function outputRatioFor(key: CropRatioKey, frameRatio: number): number | null {
  switch (key) {
    case '16:9':
      return 16 / 9
    case '21:9':
      return 21 / 9
    case '4:3':
      return 4 / 3
    case '9:16':
      return 9 / 16
    case 'original':
      return frameRatio
    case 'free':
      return null
  }
}

// THE one that silently ships wrong. The rect is normalized to the frame, so a "16:9 output" on a
// 21:9 frame is NOT width/height = 16/9 — it's width/height = targetRatio / frameRatio. Every
// ratio helper therefore takes frameRatio; none of them can be written without it.
//
// Worked example: frame 3440x1440 (frameRatio 2.389), target 16:9 (1.778). A full-height rect
// needs rectW/rectH = 1.778/2.389 = 0.744 — i.e. 2560 of 3440 px. Using 1.778 directly would
// produce a 6115px-wide rect on a 3440px frame.
export function rectRatioFor(key: CropRatioKey, frameRatio: number): number | null {
  const output = outputRatioFor(key, frameRatio)
  if (output === null || !Number.isFinite(frameRatio) || frameRatio <= 0) return null
  return output / frameRatio
}

function clamp01(v: number): number {
  return Math.min(Math.max(v, 0), 1)
}

// Forces a rect to `rectRatio` (width/height in normalized frame space), keeping it inside the
// frame. Shrinks rather than grows so a locked rect can never overhang: growing to hit the ratio
// would push an edge the user just dragged past the frame boundary.
export function applyRectRatio(rect: CropRect, rectRatio: number | null): CropRect {
  if (rectRatio === null || !Number.isFinite(rectRatio) || rectRatio <= 0) return rect

  let width = rect.width
  let height = width / rectRatio
  if (height > 1) {
    height = 1
    width = height * rectRatio
  }
  if (width > 1) {
    width = 1
    height = width / rectRatio
  }

  // Keep the rect's centre where the user put it, then pull it back inside the frame.
  const cx = rect.x + rect.width / 2
  const cy = rect.y + rect.height / 2
  const x = Math.min(Math.max(cx - width / 2, 0), 1 - width)
  const y = Math.min(Math.max(cy - height / 2, 0), 1 - height)

  return { x, y, width, height }
}

// The largest rect of the given ratio, centred in the frame. Used when a preset is picked and
// when the cropper first seeds.
export function maxRectForRatio(rectRatio: number | null): CropRect {
  if (rectRatio === null || !Number.isFinite(rectRatio) || rectRatio <= 0) {
    return { x: 0, y: 0, width: 1, height: 1 }
  }
  return applyRectRatio({ x: 0, y: 0, width: 1, height: 1 }, rectRatio)
}

// Clamps a rect into the frame and enforces the minimum extent. Always returns a rect the server
// will accept, so the component never has to guard its model separately.
export function clampRect(rect: CropRect): CropRect {
  const width = Math.min(Math.max(rect.width, MIN_CROP_EXTENT), 1)
  const height = Math.min(Math.max(rect.height, MIN_CROP_EXTENT), 1)
  return {
    x: clamp01(Math.min(rect.x, 1 - width)),
    y: clamp01(Math.min(rect.y, 1 - height)),
    width,
    height,
  }
}

export function isCropChanged(rect: CropRect): boolean {
  return (
    rect.x > CROP_CHANGED_EPS ||
    rect.y > CROP_CHANGED_EPS ||
    rect.width < 1 - CROP_CHANGED_EPS ||
    rect.height < 1 - CROP_CHANGED_EPS
  )
}

// The v-model value: a rect only when the user actually cropped something; null for the full
// frame, so callers never send a no-op crop that would cost a re-encode for no change.
export function toCropModel(rect: CropRect): CropRect | null {
  return isCropChanged(rect) ? rect : null
}

export const FULL_FRAME: CropRect = { x: 0, y: 0, width: 1, height: 1 }

// Initial rect once metadata loads: restore a previously picked crop when it's still valid
// (navigating back to the crop tab), else the full frame.
export function seedCropRect(model: CropRect | null): CropRect {
  if (!model) return { ...FULL_FRAME }
  const values = [model.x, model.y, model.width, model.height]
  if (!values.every((v) => typeof v === 'number' && Number.isFinite(v))) return { ...FULL_FRAME }
  if (model.width < MIN_CROP_EXTENT || model.height < MIN_CROP_EXTENT) return { ...FULL_FRAME }
  return clampRect(model)
}

// The 8 resize handles plus the body. Named by the edge(s) they move so a drag can resolve
// which sides are anchored without a lookup table.
export type CropHandle = 'nw' | 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w' | 'move'

export const CROP_HANDLES: CropHandle[] = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w']

// Anchor points of each handle in normalized rect space, so hit-testing and rendering agree on
// where a handle sits.
export function handlePosition(handle: CropHandle, rect: CropRect): { x: number; y: number } {
  const left = rect.x
  const right = rect.x + rect.width
  const top = rect.y
  const bottom = rect.y + rect.height
  const midX = rect.x + rect.width / 2
  const midY = rect.y + rect.height / 2
  switch (handle) {
    case 'nw':
      return { x: left, y: top }
    case 'n':
      return { x: midX, y: top }
    case 'ne':
      return { x: right, y: top }
    case 'e':
      return { x: right, y: midY }
    case 'se':
      return { x: right, y: bottom }
    case 's':
      return { x: midX, y: bottom }
    case 'sw':
      return { x: left, y: bottom }
    case 'w':
      return { x: left, y: midY }
    case 'move':
      return { x: midX, y: midY }
  }
}

// Absolute resize: the dragged edge follows the pointer and the opposite edge anchors. Returns a
// clamped, ratio-corrected rect.
export function resizeRect(
  rect: CropRect,
  handle: CropHandle,
  pointer: { x: number; y: number },
  rectRatio: number | null,
): CropRect {
  if (handle === 'move') return rect

  let left = rect.x
  let right = rect.x + rect.width
  let top = rect.y
  let bottom = rect.y + rect.height

  const px = clamp01(pointer.x)
  const py = clamp01(pointer.y)

  if (handle.includes('w')) left = Math.min(px, right - MIN_CROP_EXTENT)
  if (handle.includes('e')) right = Math.max(px, left + MIN_CROP_EXTENT)
  if (handle.includes('n')) top = Math.min(py, bottom - MIN_CROP_EXTENT)
  if (handle.includes('s')) bottom = Math.max(py, top + MIN_CROP_EXTENT)

  const next = clampRect({ x: left, y: top, width: right - left, height: bottom - top })
  return rectRatio === null ? next : clampRect(applyRectRatio(next, rectRatio))
}

// Delta-based move, deliberately: an absolute move would teleport the rect's centre to the
// pointer the instant someone presses on empty canvas, which reads as the editor throwing away
// their framing.
export function moveRect(rect: CropRect, dx: number, dy: number): CropRect {
  return {
    ...rect,
    x: Math.min(Math.max(rect.x + dx, 0), 1 - rect.width),
    y: Math.min(Math.max(rect.y + dy, 0), 1 - rect.height),
  }
}

// Nearest-hit-wins over the 8 handles, in *pixel* space so the grab radius is uniform regardless
// of the preview box's aspect ratio. Returns null when the press was further than `grabPx` from
// every handle — the caller then treats it as a body drag.
export function hitTestHandle(
  rect: CropRect,
  pointer: { x: number; y: number },
  box: { width: number; height: number },
  grabPx: number,
): CropHandle | null {
  let best: CropHandle | null = null
  let bestDist = Number.POSITIVE_INFINITY
  for (const handle of CROP_HANDLES) {
    const pos = handlePosition(handle, rect)
    const dx = (pos.x - pointer.x) * box.width
    const dy = (pos.y - pointer.y) * box.height
    const dist = Math.hypot(dx, dy)
    if (dist <= grabPx && dist < bestDist) {
      best = handle
      bestDist = dist
    }
  }
  return best
}

// Output pixel dimensions for a rect against a known source frame, rounded to even (the server
// snaps the same way for yuv420p chroma alignment, so this is what the user will actually get).
export function outputSize(
  rect: CropRect,
  frameWidth: number,
  frameHeight: number,
): { width: number; height: number } {
  const even = (v: number) => Math.max(2, Math.floor(v / 2) * 2)
  return {
    width: even(rect.width * frameWidth),
    height: even(rect.height * frameHeight),
  }
}

// How far the resulting output is from 16:9, as a ratio of ratios. Feeds the cropper's inline
// note about feed thumbnails, which stay aspect-video object-cover and will centre-crop anything
// far from 16:9.
export function isFarFromWidescreen(rect: CropRect, frameRatio: number): boolean {
  const outputRatio = (rect.width * frameRatio) / rect.height
  if (!Number.isFinite(outputRatio) || outputRatio <= 0) return false
  const widescreen = 16 / 9
  const drift = outputRatio > widescreen ? outputRatio / widescreen : widescreen / outputRatio
  return drift > 1.15
}

// Inline style for a box that shows only the cropped window of a full-frame image or video.
// Scales the media up by 1/width and 1/height and shifts it so the rect fills the container —
// lets the describe-step preview reflect the crop without re-encoding anything client-side.
export function cropWindowStyle(rect: CropRect | null): Record<string, string> {
  if (!rect) return {}
  return {
    width: `${(100 / rect.width).toFixed(4)}%`,
    height: `${(100 / rect.height).toFixed(4)}%`,
    left: `${(-(rect.x / rect.width) * 100).toFixed(4)}%`,
    top: `${(-(rect.y / rect.height) * 100).toFixed(4)}%`,
  }
}
