import { describe, it, expect } from 'vitest'
import { safeImageUrl } from '../url'

describe('safeImageUrl()', () => {
  it('passes https URLs through', () => {
    expect(safeImageUrl('https://cdn.example.com/avatar.png')).toBe(
      'https://cdn.example.com/avatar.png',
    )
  })

  it('passes http URLs through', () => {
    expect(safeImageUrl('http://example.com/a.png')).toBe('http://example.com/a.png')
  })

  it('passes data:image URLs through', () => {
    expect(safeImageUrl('data:image/png;base64,iVBORw0KGgo=')).toBe(
      'data:image/png;base64,iVBORw0KGgo=',
    )
  })

  it('returns null for non-image data URLs', () => {
    // `data:text/html,...` is harmless in an <img>, but rejecting it keeps the
    // helper safe to reuse in <iframe src> etc.
    expect(safeImageUrl('data:text/html,<script>alert(1)</script>')).toBeNull()
  })

  it('returns null for javascript: URLs', () => {
    expect(safeImageUrl('javascript:alert(1)')).toBeNull()
  })

  it('returns null for unknown schemes', () => {
    expect(safeImageUrl('ftp://example.com/a.png')).toBeNull()
  })

  it('returns null for null/undefined/empty', () => {
    expect(safeImageUrl(null)).toBeNull()
    expect(safeImageUrl(undefined)).toBeNull()
    expect(safeImageUrl('')).toBeNull()
  })

  it('resolves protocol-relative URLs against the page origin', () => {
    // `//cdn.example.com/x.png` should be treated as same-protocol, which on
    // jsdom defaults to http: — so this passes.
    expect(safeImageUrl('//cdn.example.com/x.png')).toBe('//cdn.example.com/x.png')
  })
})
