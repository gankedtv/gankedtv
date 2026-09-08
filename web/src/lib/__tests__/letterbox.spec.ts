import { describe, it, expect } from 'vitest'
import { contentFillTransform, detectContentRect, LETTERBOX_MIN_BAR, type Rgba } from '../letterbox'
import type { CropRect } from '../crop'

// Build a frame of `width` x `height` with a solid bar border of the given thickness and a
// bright interior, in the RGBA layout canvas hands back from getImageData.
function frame(opts: {
  width: number
  height: number
  top?: number
  bottom?: number
  left?: number
  right?: number
  barLuma?: number
  contentLuma?: number
}): Rgba {
  const { width, height, top = 0, bottom = 0, left = 0, right = 0 } = opts
  const bar = opts.barLuma ?? 0
  const content = opts.contentLuma ?? 200
  const data = new Uint8ClampedArray(width * height * 4)
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const inside = y >= top && y < height - bottom && x >= left && x < width - right
      const v = inside ? content : bar
      const i = (y * width + x) * 4
      data[i] = v
      data[i + 1] = v
      data[i + 2] = v
      data[i + 3] = 255
    }
  }
  return { data, width, height }
}

describe('detectContentRect — pillarbox', () => {
  it('finds the content rect of a side-barred frame as 0..1 fractions', () => {
    const rect = detectContentRect(frame({ width: 100, height: 100, left: 20, right: 20 }))
    expect(rect).not.toBeNull()
    expect(rect!.x).toBeCloseTo(0.2, 2)
    expect(rect!.width).toBeCloseTo(0.6, 2)
    expect(rect!.y).toBeCloseTo(0, 2)
    expect(rect!.height).toBeCloseTo(1, 2)
  })
})

describe('detectContentRect — letterbox', () => {
  it('finds the content rect of a top/bottom-barred frame', () => {
    const rect = detectContentRect(frame({ width: 100, height: 100, top: 25, bottom: 25 }))
    expect(rect).not.toBeNull()
    expect(rect!.y).toBeCloseTo(0.25, 2)
    expect(rect!.height).toBeCloseTo(0.5, 2)
    expect(rect!.x).toBeCloseTo(0, 2)
    expect(rect!.width).toBeCloseTo(1, 2)
  })

  it('handles bars on all four sides at once', () => {
    const rect = detectContentRect(
      frame({ width: 100, height: 100, top: 10, bottom: 10, left: 15, right: 15 }),
    )
    expect(rect).not.toBeNull()
    expect(rect!.x).toBeCloseTo(0.15, 2)
    expect(rect!.y).toBeCloseTo(0.1, 2)
    expect(rect!.width).toBeCloseTo(0.7, 2)
    expect(rect!.height).toBeCloseTo(0.8, 2)
  })
})

describe('detectContentRect — refusals', () => {
  it('returns null for a clean frame with no bars', () => {
    expect(detectContentRect(frame({ width: 100, height: 100 }))).toBeNull()
  })

  it('ignores sub-threshold slivers so encoder noise does not trigger a zoom', () => {
    // Bars totalling under LETTERBOX_MIN_BAR of the axis. Sized off a 400px frame because a
    // 100px one can't express a two-sided trim finer than the threshold itself.
    const edge = 400
    const sliver = Math.floor((LETTERBOX_MIN_BAR * edge) / 2) - 1
    expect(
      detectContentRect(frame({ width: edge, height: edge, left: sliver, right: sliver })),
    ).toBeNull()
  })

  it('returns null for a fully black frame rather than cropping to nothing', () => {
    expect(detectContentRect(frame({ width: 100, height: 100, contentLuma: 0 }))).toBeNull()
  })

  // The poster is one frame. A dark scene can read as bars on every edge, and trusting it would
  // zoom permanently into real gameplay — so an implausibly small survivor is refused outright.
  it('refuses a detection that would eat most of the frame', () => {
    expect(
      detectContentRect(
        frame({ width: 100, height: 100, top: 45, bottom: 45, left: 45, right: 45 }),
      ),
    ).toBeNull()
  })

  // Baked bars are #000; a dim night scene is not. Keeping the gate tight is what stops a dark
  // poster from being read as a pillarbox.
  it('does not treat merely dim pixels as bars', () => {
    expect(
      detectContentRect(frame({ width: 100, height: 100, left: 20, right: 20, barLuma: 40 })),
    ).toBeNull()
  })

  it('returns null for an empty frame', () => {
    expect(detectContentRect({ data: new Uint8ClampedArray(0), width: 0, height: 0 })).toBeNull()
  })
})

describe('contentFillTransform', () => {
  // A 1920x1080 frame in the 9:16 reels column: contain-fits to 400x225.
  const SLOT_W = 400
  const SLOT_H = 800
  const FRAME_W = 1920
  const FRAME_H = 1080

  function transform(rect: CropRect) {
    return contentFillTransform(rect, FRAME_W, FRAME_H, SLOT_W, SLOT_H)
  }

  it('scales symmetric pillarbox content out to the full column width', () => {
    // Content is 70% of the 400px box, so filling the column takes 1/0.7.
    expect(transform({ x: 0.15, y: 0, width: 0.7, height: 1 })).toBe(
      'translate(0.0000%, 0.0000%) scale(1.4286)',
    )
  })

  it('shifts content that sits off-centre back to the middle', () => {
    expect(transform({ x: 0.3, y: 0, width: 0.6, height: 1 })).toBe(
      'translate(-16.6667%, 0.0000%) scale(1.6667)',
    )
  })

  // Height binds when removing the bars would otherwise overflow the column vertically.
  it('never scales past what the shorter axis allows', () => {
    const out = transform({ x: 0.4, y: 0.4, width: 0.5, height: 0.5 })
    // Height: 800 / (0.5 * 225) = 7.11; width: 400 / (0.5 * 400) = 2. Width is the binding cap.
    expect(out).toContain('scale(2.0000)')
  })

  it('declines a rect that is already the full frame', () => {
    expect(transform({ x: 0, y: 0, width: 1, height: 1 })).toBeNull()
  })

  it('declines when the frame never got probed dimensions', () => {
    const rect = { x: 0.15, y: 0, width: 0.7, height: 1 }
    expect(contentFillTransform(rect, null, null, SLOT_W, SLOT_H)).toBeNull()
    expect(contentFillTransform(rect, FRAME_W, null, SLOT_W, SLOT_H)).toBeNull()
  })

  it('declines before the slot has been laid out', () => {
    expect(
      contentFillTransform({ x: 0.15, y: 0, width: 0.7, height: 1 }, FRAME_W, FRAME_H, 0, 0),
    ).toBeNull()
  })
})
