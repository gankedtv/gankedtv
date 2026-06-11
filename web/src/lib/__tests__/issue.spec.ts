import { describe, it, expect } from 'vitest'
import { issueNumber, formatIssueNo, volIssMeta } from '../issue'

describe('issueNumber', () => {
  it('maps numeric ids deterministically with zero-padding', () => {
    expect(issueNumber(41)).toBe('042')
    expect(issueNumber(0)).toBe('001')
    expect(issueNumber('41')).toBe('042')
  })

  it('wraps within the 3-digit range', () => {
    expect(issueNumber(999)).toBe('001')
    expect(issueNumber(1000)).toBe('002')
  })

  it('hashes non-numeric ids deterministically', () => {
    const a = issueNumber('a1b2c3-uuid')
    expect(a).toMatch(/^\d{3}$/)
    expect(issueNumber('a1b2c3-uuid')).toBe(a)
    expect(issueNumber('other-id')).not.toBe('')
  })
})

describe('formatIssueNo', () => {
  it('prefixes with No.', () => {
    expect(formatIssueNo(41)).toBe('No. 042')
  })
})

describe('volIssMeta', () => {
  it('formats volume, day-of-year issue, and date from UTC', () => {
    expect(volIssMeta(new Date(Date.UTC(2026, 5, 11)))).toBe('VOL 1 · ISS 162 · 06.11.26')
  })

  it('increments the volume each year and never drops below 1', () => {
    expect(volIssMeta(new Date(Date.UTC(2027, 0, 1)))).toBe('VOL 2 · ISS 001 · 01.01.27')
    expect(volIssMeta(new Date(Date.UTC(2025, 0, 1)))).toContain('VOL 1')
  })
})
