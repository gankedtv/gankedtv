// Client-side black-bar (letterbox / pillarbox) detection, split out of the reels slot so the
// threshold logic is unit-testable without a media-capable DOM. Mirrors the lib/crop.ts split.
//
// Why the client detects at all: reels renders into a 9:16 column, so a master with bars baked
// in pays twice — once for its own bars, once for the letterboxing the column adds — and the
// clip ends up a stamp in the middle of the screen. The server already knows how to find bars
// (GET /clips/{id}/crop-suggestion), but that route is owner-only and forks ffmpeg per call,
// which a scrolling feed can't pay for. So this reads pixels the browser already has.
//
// This never mutates the clip. It only changes how the existing master is framed on screen.

import type { CropRect } from './crop'

export interface Rgba {
  data: Uint8ClampedArray
  width: number
  height: number
}

// Baked bars encode to near-#000; a dim night scene does not. This gate is the main thing
// standing between "removed the pillarbox" and "zoomed into someone's gameplay", so it stays
// tight — a missed detection just leaves today's framing, a false one is visible and permanent.
const BAR_LUMA_MAX = 12

// Bars thinner than this are rounding noise from a lossy encode, not framing worth undoing.
export const LETTERBOX_MIN_BAR = 0.02

// A single poster frame can't distinguish a pillarbox from a fade-to-black, so a detection that
// would leave less than this fraction of either axis is refused rather than trusted.
const MIN_CONTENT_FRACTION = 0.4

function isDark(px: Rgba, x: number, y: number): boolean {
  const i = (y * px.width + x) * 4
  // Rec. 709 luma. Alpha is ignored: canvas frames from a decoded video are always opaque.
  const luma = 0.2126 * px.data[i] + 0.7152 * px.data[i + 1] + 0.0722 * px.data[i + 2]
  return luma <= BAR_LUMA_MAX
}

function rowIsDark(px: Rgba, y: number, x0: number, x1: number): boolean {
  for (let x = x0; x <= x1; x++) if (!isDark(px, x, y)) return false
  return true
}

function colIsDark(px: Rgba, x: number, y0: number, y1: number): boolean {
  for (let y = y0; y <= y1; y++) if (!isDark(px, x, y)) return false
  return true
}

// Returns the content bounding box as 0..1 fractions of the frame, or null when there is nothing
// worth reframing (no bars, an all-black frame, or a detection too aggressive to trust).
export function detectContentRect(px: Rgba): CropRect | null {
  const { width, height } = px
  if (width < 2 || height < 2 || px.data.length < width * height * 4) return null

  let top = 0
  while (top < height && rowIsDark(px, top, 0, width - 1)) top++
  // Every row was dark: an all-black frame carries no content rect to find.
  if (top === height) return null

  let bottom = height - 1
  while (bottom > top && rowIsDark(px, bottom, 0, width - 1)) bottom--

  // Columns are scanned only across the surviving rows. Including the horizontal bars would make
  // every column read dark at its ends and understate the pillarbox.
  let left = 0
  while (left < width && colIsDark(px, left, top, bottom)) left++
  if (left === width) return null

  let right = width - 1
  while (right > left && colIsDark(px, right, top, bottom)) right--

  const contentW = right - left + 1
  const contentH = bottom - top + 1
  const fw = contentW / width
  const fh = contentH / height
  if (fw < MIN_CONTENT_FRACTION || fh < MIN_CONTENT_FRACTION) return null

  // Nothing meaningful trimmed on either axis — leave the framing alone.
  if (1 - fw < LETTERBOX_MIN_BAR && 1 - fh < LETTERBOX_MIN_BAR) return null

  return { x: left / width, y: top / height, width: fw, height: fh }
}

// Longest edge the frame is sampled at. Detection only needs bar boundaries, and downscaling
// both bounds the per-clip cost and averages out encode noise along the bar edge. The averaging
// biases the boundary toward the bar side, which leaves a sliver rather than eating content.
const SAMPLE_MAX_EDGE = 200

// Best-effort: reads the clip's poster and reports its content rect, or null if anything at all
// gets in the way. The poster is generated from the same master the reel plays, so its bars are
// the master's bars.
//
// It deliberately reads the POSTER and not the playing <video>. Canvas readback needs
// crossOrigin, and setting that on the element the user is watching fails the load outright when
// the storage bucket serves no CORS headers — trading a cosmetic win for a black screen. A
// separate Image can fail all it likes; playback never sees it.
export function detectPosterBars(url: string): Promise<CropRect | null> {
  return new Promise((resolve) => {
    if (typeof document === 'undefined' || !url) {
      resolve(null)
      return
    }
    const img = new Image()
    img.crossOrigin = 'anonymous'
    img.decoding = 'async'
    img.onload = () => {
      try {
        const w = img.naturalWidth
        const h = img.naturalHeight
        if (!w || !h) {
          resolve(null)
          return
        }
        const scale = Math.min(1, SAMPLE_MAX_EDGE / Math.max(w, h))
        const cw = Math.max(2, Math.round(w * scale))
        const ch = Math.max(2, Math.round(h * scale))
        const canvas = document.createElement('canvas')
        canvas.width = cw
        canvas.height = ch
        const ctx = canvas.getContext('2d', { willReadFrequently: true })
        if (!ctx) {
          resolve(null)
          return
        }
        ctx.drawImage(img, 0, 0, cw, ch)
        // Throws SecurityError on a tainted canvas — i.e. the bucket sent no CORS headers.
        const frame = ctx.getImageData(0, 0, cw, ch)
        resolve(detectContentRect({ data: frame.data, width: cw, height: ch }))
      } catch {
        resolve(null)
      }
    }
    img.onerror = () => resolve(null)
    img.src = url
  })
}

// Scales a frame so its CONTENT box — not its padded frame — fits the slot, and re-centres it.
// Returns a CSS transform, or null when there is nothing to gain.
//
// Derived entirely from the slot size and the frame's declared aspect: the video element's own
// box is never measured, so a caller can't race the media load. translate() percentages resolve
// against the element's own unscaled box, which is what keeps the recentring measurement-free.
// Frame dimensions are nullable on the wire (a clip whose probe never landed), so the guard
// lives here rather than at each call site.
export function contentFillTransform(
  rect: CropRect,
  frameWidth: number | null,
  frameHeight: number | null,
  slotWidth: number,
  slotHeight: number,
): string | null {
  if (!frameWidth || !frameHeight || frameWidth <= 0 || frameHeight <= 0) return null
  if (slotWidth <= 0 || slotHeight <= 0) return null
  if (rect.width <= 0 || rect.height <= 0) return null

  const ratio = frameWidth / frameHeight
  // The contain-fit box the frame occupies before any reframing.
  const boxWidth = Math.min(slotWidth, slotHeight * ratio)
  const boxHeight = boxWidth / ratio
  if (boxWidth <= 0 || boxHeight <= 0) return null

  const scale = Math.min(
    slotWidth / (rect.width * boxWidth),
    slotHeight / (rect.height * boxHeight),
  )
  if (!Number.isFinite(scale) || scale <= 1.001) return null

  const tx = -scale * (rect.x + rect.width / 2 - 0.5) * 100
  const ty = -scale * (rect.y + rect.height / 2 - 0.5) * 100
  return `translate(${tx.toFixed(4)}%, ${ty.toFixed(4)}%) scale(${scale.toFixed(4)})`
}
