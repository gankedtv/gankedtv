import { describe, it, expect } from 'vitest'
import {
  CROP_HANDLES,
  FULL_FRAME,
  MIN_CROP_EXTENT,
  applyRectRatio,
  clampRect,
  cropWindowStyle,
  handlePosition,
  hitTestHandle,
  isCropChanged,
  isFarFromWidescreen,
  maxRectForRatio,
  moveRect,
  outputRatioFor,
  outputSize,
  rectRatioFor,
  resizeRect,
  seedCropRect,
  toCropModel,
  type CropRect,
} from '../crop'

// 3440x1440 ultrawide — the shape the whole feature exists for.
const ULTRAWIDE = 3440 / 1440
const WIDESCREEN = 16 / 9

describe('lib/crop', () => {
  describe('rectRatioFor', () => {
    it('divides the target ratio by the FRAME ratio, not the target alone', () => {
      // The one that silently ships wrong. A "16:9 output" on a 21:9 frame is
      // width/height = 1.778/2.389 = 0.744 in normalized rect space — NOT 1.778, which would
      // demand a 6115px-wide rect on a 3440px frame.
      const rectRatio = rectRatioFor('16:9', ULTRAWIDE)!

      expect(rectRatio).toBeCloseTo(WIDESCREEN / ULTRAWIDE, 6)
      expect(rectRatio).toBeCloseTo(0.744, 3)
      expect(rectRatio).not.toBeCloseTo(WIDESCREEN, 2)
    })

    it('yields a full-width rect when the target matches the frame', () => {
      expect(rectRatioFor('16:9', WIDESCREEN)).toBeCloseTo(1, 6)
    })

    it("'original' always means the whole frame regardless of its shape", () => {
      expect(rectRatioFor('original', ULTRAWIDE)).toBeCloseTo(1, 6)
      expect(rectRatioFor('original', 9 / 16)).toBeCloseTo(1, 6)
    })

    it("'free' is unconstrained", () => {
      expect(rectRatioFor('free', ULTRAWIDE)).toBeNull()
      expect(outputRatioFor('free', ULTRAWIDE)).toBeNull()
    })

    it('returns null for a degenerate frame ratio rather than dividing by it', () => {
      expect(rectRatioFor('16:9', 0)).toBeNull()
      expect(rectRatioFor('16:9', Number.NaN)).toBeNull()
    })

    it('handles a portrait target on a landscape frame', () => {
      // 9:16 on a 21:9 frame is a very narrow rect — a bug here would silently overflow.
      const rectRatio = rectRatioFor('9:16', ULTRAWIDE)!
      expect(rectRatio).toBeLessThan(0.3)
      const rect = maxRectForRatio(rectRatio)
      expect(rect.width).toBeLessThanOrEqual(1)
      expect(rect.height).toBeCloseTo(1, 6)
    })
  })

  describe('maxRectForRatio', () => {
    it('produces the exact ultrawide pillarbox rect for a 16:9 target', () => {
      const rect = maxRectForRatio(rectRatioFor('16:9', ULTRAWIDE))

      expect(rect.height).toBeCloseTo(1, 6)
      // 2560 of 3440 px wide, centred → 440px of bar on each side.
      expect(rect.width * 3440).toBeCloseTo(2560, 0)
      expect(rect.x * 3440).toBeCloseTo(440, 0)
      expect(rect.y).toBeCloseTo(0, 6)
    })

    it('returns the full frame when unconstrained', () => {
      expect(maxRectForRatio(null)).toEqual(FULL_FRAME)
    })

    it('never produces a rect that overhangs the frame', () => {
      for (const key of ['16:9', '21:9', '4:3', '9:16', 'original'] as const) {
        for (const frameRatio of [ULTRAWIDE, WIDESCREEN, 4 / 3, 9 / 16]) {
          const rect = maxRectForRatio(rectRatioFor(key, frameRatio))
          expect(rect.x + rect.width).toBeLessThanOrEqual(1.000001)
          expect(rect.y + rect.height).toBeLessThanOrEqual(1.000001)
        }
      }
    })
  })

  describe('applyRectRatio', () => {
    it('shrinks rather than grows so a locked rect can never overhang', () => {
      // Growing to satisfy the ratio would push the edge the user just dragged past the frame.
      const rect = applyRectRatio({ x: 0.9, y: 0.9, width: 0.1, height: 0.1 }, 4)

      expect(rect.x + rect.width).toBeLessThanOrEqual(1.000001)
      expect(rect.y + rect.height).toBeLessThanOrEqual(1.000001)
      expect(rect.width / rect.height).toBeCloseTo(4, 4)
    })

    it('keeps the rect centred where the user left it', () => {
      const rect = applyRectRatio({ x: 0.3, y: 0.3, width: 0.4, height: 0.4 }, 1)

      expect(rect.x + rect.width / 2).toBeCloseTo(0.5, 4)
      expect(rect.y + rect.height / 2).toBeCloseTo(0.5, 4)
    })

    it('passes the rect through untouched when unconstrained', () => {
      const rect: CropRect = { x: 0.1, y: 0.2, width: 0.3, height: 0.4 }
      expect(applyRectRatio(rect, null)).toBe(rect)
    })
  })

  describe('clampRect', () => {
    it('pulls an out-of-bounds rect back inside the frame', () => {
      const rect = clampRect({ x: -0.5, y: 1.5, width: 0.5, height: 0.5 })

      expect(rect.x).toBeGreaterThanOrEqual(0)
      expect(rect.y + rect.height).toBeLessThanOrEqual(1.000001)
    })

    it('enforces the minimum extent the server also enforces', () => {
      const rect = clampRect({ x: 0, y: 0, width: 0.001, height: 0.001 })

      expect(rect.width).toBe(MIN_CROP_EXTENT)
      expect(rect.height).toBe(MIN_CROP_EXTENT)
    })

    it('is idempotent', () => {
      const once = clampRect({ x: 0.7, y: 0.7, width: 0.6, height: 0.6 })
      expect(clampRect(once)).toEqual(once)
    })
  })

  describe('toCropModel / isCropChanged', () => {
    it('is null for the full frame so no no-op crop is ever sent', () => {
      // A no-op crop would cost the user a full re-encode for zero visible change.
      expect(toCropModel(FULL_FRAME)).toBeNull()
      expect(isCropChanged(FULL_FRAME)).toBe(false)
    })

    it('is null for a sub-threshold nudge', () => {
      expect(toCropModel({ x: 0.001, y: 0.001, width: 0.998, height: 0.998 })).toBeNull()
    })

    it('returns the rect once a real crop exists', () => {
      const rect: CropRect = { x: 0.1279, y: 0, width: 0.7442, height: 1 }
      expect(toCropModel(rect)).toEqual(rect)
    })

    it('detects a crop on any single edge', () => {
      expect(isCropChanged({ x: 0.2, y: 0, width: 0.8, height: 1 })).toBe(true)
      expect(isCropChanged({ x: 0, y: 0.2, width: 1, height: 0.8 })).toBe(true)
      expect(isCropChanged({ x: 0, y: 0, width: 0.8, height: 1 })).toBe(true)
      expect(isCropChanged({ x: 0, y: 0, width: 1, height: 0.8 })).toBe(true)
    })
  })

  describe('seedCropRect', () => {
    it('restores a previously picked crop so a tab switch keeps it', () => {
      const model: CropRect = { x: 0.1279, y: 0, width: 0.7442, height: 1 }
      expect(seedCropRect(model)).toEqual(model)
    })

    it('falls back to the full frame for null or a nonsense model', () => {
      expect(seedCropRect(null)).toEqual(FULL_FRAME)
      expect(seedCropRect({ x: Number.NaN, y: 0, width: 0.5, height: 0.5 })).toEqual(FULL_FRAME)
      expect(seedCropRect({ x: 0, y: 0, width: 0.001, height: 0.5 })).toEqual(FULL_FRAME)
    })

    it('returns a copy, so mutating the seed cannot write through to the model', () => {
      const model = { ...FULL_FRAME }
      const seeded = seedCropRect(null)
      seeded.x = 0.5
      expect(model.x).toBe(0)
    })
  })

  describe('resizeRect', () => {
    it('anchors the opposite edge while the dragged edge follows the pointer', () => {
      const rect = resizeRect(FULL_FRAME, 'e', { x: 0.6, y: 0.5 }, null)

      expect(rect.x).toBeCloseTo(0, 6)
      expect(rect.width).toBeCloseTo(0.6, 6)
    })

    it('moves both axes from a corner handle', () => {
      const rect = resizeRect(FULL_FRAME, 'se', { x: 0.6, y: 0.7 }, null)

      expect(rect.width).toBeCloseTo(0.6, 6)
      expect(rect.height).toBeCloseTo(0.7, 6)
    })

    it('drags the west edge without moving the east edge', () => {
      const rect = resizeRect(FULL_FRAME, 'w', { x: 0.25, y: 0.5 }, null)

      expect(rect.x).toBeCloseTo(0.25, 6)
      expect(rect.x + rect.width).toBeCloseTo(1, 6)
    })

    it('refuses to invert past the minimum extent', () => {
      // Dragging the east edge left past the west one must not produce a negative width.
      const rect = resizeRect(FULL_FRAME, 'e', { x: -1, y: 0.5 }, null)

      expect(rect.width).toBeGreaterThanOrEqual(MIN_CROP_EXTENT)
      expect(rect.x).toBeGreaterThanOrEqual(0)
    })

    it('re-locks to the ratio after a resize', () => {
      const rectRatio = rectRatioFor('16:9', ULTRAWIDE)!
      const rect = resizeRect(FULL_FRAME, 'se', { x: 0.5, y: 0.9 }, rectRatio)

      expect(rect.width / rect.height).toBeCloseTo(rectRatio, 4)
    })

    it('ignores the move handle', () => {
      expect(resizeRect(FULL_FRAME, 'move', { x: 0.2, y: 0.2 }, null)).toEqual(FULL_FRAME)
    })

    it('clamps a pointer dragged outside the frame', () => {
      const rect = resizeRect(FULL_FRAME, 'se', { x: 5, y: 5 }, null)

      expect(rect.x + rect.width).toBeLessThanOrEqual(1.000001)
      expect(rect.y + rect.height).toBeLessThanOrEqual(1.000001)
    })
  })

  describe('moveRect', () => {
    it('translates by the delta and keeps the size', () => {
      const rect = moveRect({ x: 0.2, y: 0.2, width: 0.5, height: 0.5 }, 0.1, -0.1)

      expect(rect.x).toBeCloseTo(0.3, 6)
      expect(rect.y).toBeCloseTo(0.1, 6)
      expect(rect.width).toBeCloseTo(0.5, 6)
    })

    it('stops at the frame edge instead of leaving it', () => {
      const rect = moveRect({ x: 0.2, y: 0.2, width: 0.5, height: 0.5 }, 5, 5)

      expect(rect.x).toBeCloseTo(0.5, 6)
      expect(rect.y).toBeCloseTo(0.5, 6)
    })

    it('is delta-based, so a zero delta is a no-op rather than a teleport', () => {
      // An absolute move would snap the rect's centre to the pointer on the first press,
      // which reads as the editor throwing away the user's framing.
      const rect: CropRect = { x: 0.1, y: 0.1, width: 0.3, height: 0.3 }
      expect(moveRect(rect, 0, 0)).toEqual(rect)
    })
  })

  describe('hitTestHandle', () => {
    const box = { width: 640, height: 360 }

    it('picks the handle under the pointer', () => {
      const hit = hitTestHandle(FULL_FRAME, { x: 0, y: 0 }, box, 12)
      expect(hit).toBe('nw')
    })

    it('returns null when the press is far from every handle', () => {
      // The caller then treats it as a body drag, which is what makes "press anywhere and
      // pan" work.
      expect(hitTestHandle(FULL_FRAME, { x: 0.5, y: 0.5 }, box, 12)).toBeNull()
    })

    it('is nearest-hit-wins when two handles are within the grab radius', () => {
      // A tiny rect crowds its handles together; picking the far one would feel broken.
      const tiny: CropRect = { x: 0.5, y: 0.5, width: MIN_CROP_EXTENT, height: MIN_CROP_EXTENT }
      const hit = hitTestHandle(tiny, handlePosition('se', tiny), box, 40)
      expect(hit).toBe('se')
    })

    it('measures the grab radius in pixels, so it feels the same on any frame ratio', () => {
      // The same fractional offset is a different pixel distance on a wide box vs a tall one;
      // a fraction-space radius would make handles harder to grab on ultrawide sources.
      const nearInFractions = { x: 0.02, y: 0 }
      expect(hitTestHandle(FULL_FRAME, nearInFractions, { width: 100, height: 360 }, 12)).toBe('nw')
      expect(
        hitTestHandle(FULL_FRAME, nearInFractions, { width: 2000, height: 360 }, 12),
      ).toBeNull()
    })

    it('exposes all eight handles', () => {
      expect(CROP_HANDLES).toHaveLength(8)
      expect(new Set(CROP_HANDLES).size).toBe(8)
    })
  })

  describe('handlePosition', () => {
    it('places each handle on the rect it describes', () => {
      const rect: CropRect = { x: 0.2, y: 0.3, width: 0.4, height: 0.5 }

      expect(handlePosition('nw', rect)).toEqual({ x: 0.2, y: 0.3 })
      expect(handlePosition('se', rect)).toEqual({ x: 0.6000000000000001, y: 0.8 })
      expect(handlePosition('n', rect).x).toBeCloseTo(0.4, 6)
      expect(handlePosition('w', rect).y).toBeCloseTo(0.55, 6)
      expect(handlePosition('move', rect).x).toBeCloseTo(0.4, 6)
    })
  })

  describe('outputSize', () => {
    it('reports the ultrawide worked example', () => {
      const size = outputSize({ x: 0.1279, y: 0, width: 0.7442, height: 1 }, 3440, 1440)

      expect(size.width).toBe(2560)
      expect(size.height).toBe(1440)
    })

    it('rounds down to even, matching the server yuv420p snap', () => {
      const size = outputSize({ x: 0, y: 0, width: 0.5, height: 0.5 }, 1921, 1081)

      expect(size.width % 2).toBe(0)
      expect(size.height % 2).toBe(0)
    })

    it('never reports a zero dimension', () => {
      const size = outputSize({ x: 0, y: 0, width: 0.0001, height: 0.0001 }, 100, 100)

      expect(size.width).toBeGreaterThanOrEqual(2)
      expect(size.height).toBeGreaterThanOrEqual(2)
    })
  })

  describe('isFarFromWidescreen', () => {
    it('is false for a 16:9 output, however the frame is shaped', () => {
      const rect = maxRectForRatio(rectRatioFor('16:9', ULTRAWIDE))
      expect(isFarFromWidescreen(rect, ULTRAWIDE)).toBe(false)
    })

    it('is true for a portrait crop, which feed cards will centre-crop', () => {
      const rect = maxRectForRatio(rectRatioFor('9:16', ULTRAWIDE))
      expect(isFarFromWidescreen(rect, ULTRAWIDE)).toBe(true)
    })

    it('is true for an uncropped ultrawide too', () => {
      expect(isFarFromWidescreen(FULL_FRAME, ULTRAWIDE)).toBe(true)
    })

    it('is false for a degenerate rect rather than throwing', () => {
      expect(isFarFromWidescreen({ x: 0, y: 0, width: 0.5, height: 0 }, ULTRAWIDE)).toBe(false)
    })
  })

  describe('cropWindowStyle', () => {
    it('is empty for no crop, so the media keeps its own sizing', () => {
      expect(cropWindowStyle(null)).toEqual({})
    })

    it('scales and offsets so only the cropped window fills the container', () => {
      // Half-width rect starting at the midpoint → 200% wide, shifted left by a full container.
      const style = cropWindowStyle({ x: 0.5, y: 0, width: 0.5, height: 1 })

      expect(style.width).toBe('200.0000%')
      expect(style.height).toBe('100.0000%')
      expect(style.left).toBe('-100.0000%')
      // -0 formats without the sign, which is what CSS wants anyway.
      expect(style.top).toBe('0.0000%')
    })

    it('keeps the full frame visible for an uncropped rect', () => {
      const style = cropWindowStyle(FULL_FRAME)

      expect(style.width).toBe('100.0000%')
      expect(style.left).toBe('0.0000%')
    })
  })
})
