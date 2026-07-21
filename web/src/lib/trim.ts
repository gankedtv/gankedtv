// Pure trim-range logic for the pre-upload trimmer, split out of ClipTrimmer.vue so the
// regression-prone parts (clamping, null-when-whole-clip model semantics, seed restore)
// are unit-testable without a media-capable DOM.

export interface TrimRange {
  start: number
  end: number
}

// Matches the server's minimum trimmed span (ClipUploadService.MinTrimSpanSecs).
export const MIN_TRIM_GAP = 0.2
// "Did a handle actually move" threshold — same as rewynd's changed check.
export const TRIM_CHANGED_EPS = 0.05

export function clampTrimStart(t: number, end: number): number {
  return Math.min(Math.max(t, 0), end - MIN_TRIM_GAP)
}

export function clampTrimEnd(t: number, start: number, duration: number): number {
  return Math.max(Math.min(t, duration), start + MIN_TRIM_GAP)
}

export function isTrimChanged(start: number, end: number, duration: number): boolean {
  return start > TRIM_CHANGED_EPS || end < duration - TRIM_CHANGED_EPS
}

// The v-model value: a range only when the user actually cut something; null for an
// untouched/whole-clip range or an unloaded video, so callers never send a no-op trim.
export function toTrimModel(start: number, end: number, duration: number): TrimRange | null {
  return duration > 0 && isTrimChanged(start, end, duration) ? { start, end } : null
}

// Initial handle positions once metadata loads: restore a previously picked range when it
// still fits this duration (navigating back to the trim step), else the full span.
export function seedTrimRange(model: TrimRange | null, duration: number): TrimRange {
  if (
    model &&
    model.start >= 0 &&
    model.end - model.start >= MIN_TRIM_GAP &&
    model.end <= duration + TRIM_CHANGED_EPS
  ) {
    return { start: model.start, end: Math.min(model.end, duration) }
  }
  return { start: 0, end: duration }
}
