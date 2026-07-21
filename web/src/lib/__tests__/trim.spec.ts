import { describe, it, expect } from 'vitest'
import {
  MIN_TRIM_GAP,
  TRIM_CHANGED_EPS,
  clampTrimStart,
  clampTrimEnd,
  isTrimChanged,
  toTrimModel,
  seedTrimRange,
} from '../trim'

describe('lib/trim', () => {
  describe('clampTrimStart', () => {
    it('clamps below zero and above end minus the minimum gap', () => {
      expect(clampTrimStart(-3, 10)).toBe(0)
      expect(clampTrimStart(4, 10)).toBe(4)
      expect(clampTrimStart(9.95, 10)).toBeCloseTo(10 - MIN_TRIM_GAP)
    })
  })

  describe('clampTrimEnd', () => {
    it('clamps above duration and below start plus the minimum gap', () => {
      expect(clampTrimEnd(99, 2, 10)).toBe(10)
      expect(clampTrimEnd(6, 2, 10)).toBe(6)
      expect(clampTrimEnd(2.01, 2, 10)).toBeCloseTo(2 + MIN_TRIM_GAP)
    })
  })

  describe('isTrimChanged', () => {
    it('treats the full span (within epsilon) as unchanged', () => {
      expect(isTrimChanged(0, 10, 10)).toBe(false)
      expect(isTrimChanged(TRIM_CHANGED_EPS / 2, 10 - TRIM_CHANGED_EPS / 2, 10)).toBe(false)
    })

    it('detects either handle moving past the epsilon', () => {
      expect(isTrimChanged(1, 10, 10)).toBe(true)
      expect(isTrimChanged(0, 8, 10)).toBe(true)
    })
  })

  describe('toTrimModel', () => {
    it('returns null while the video has no duration yet', () => {
      expect(toTrimModel(0, 0, 0)).toBeNull()
    })

    it('returns null for an untouched whole-clip range', () => {
      expect(toTrimModel(0, 10, 10)).toBeNull()
    })

    it('returns the range once a handle moved', () => {
      expect(toTrimModel(2.5, 8, 10)).toEqual({ start: 2.5, end: 8 })
    })
  })

  describe('seedTrimRange', () => {
    it('falls back to the full span without a model', () => {
      expect(seedTrimRange(null, 10)).toEqual({ start: 0, end: 10 })
    })

    it('restores a valid previously picked range', () => {
      expect(seedTrimRange({ start: 2, end: 8 }, 10)).toEqual({ start: 2, end: 8 })
    })

    it('caps a range ending at the duration boundary (within epsilon)', () => {
      expect(seedTrimRange({ start: 2, end: 10.03 }, 10)).toEqual({ start: 2, end: 10 })
    })

    it('discards a model from a different (shorter) file', () => {
      expect(seedTrimRange({ start: 20, end: 30 }, 10)).toEqual({ start: 0, end: 10 })
    })

    it('discards a degenerate span', () => {
      expect(seedTrimRange({ start: 5, end: 5.05 }, 10)).toEqual({ start: 0, end: 10 })
    })
  })
})
