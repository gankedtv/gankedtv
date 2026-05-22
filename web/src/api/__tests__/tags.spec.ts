import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { tags } from '../tags'
import { configureAuth, BASE_URL } from '../client'

beforeEach(() => {
  configureAuth({
    getAccessToken: () => null,
    getRefreshToken: () => null,
    onTokenRefreshed: () => {},
    onRefreshFailed: () => {},
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

describe('api/tags', () => {
  describe('autocomplete()', () => {
    it('GETs /tags?prefix=… and returns the parsed list', async () => {
      const payload = [{ id: 1, slug: 'clutch', name: 'clutch', clipCount: 12 }]
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(payload)),
      )

      const result = await tags.autocomplete('clu')

      expect(result).toEqual(payload)
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/tags?prefix=clu`)
    })

    it('URL-encodes the prefix', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse([])),
      )

      await tags.autocomplete('clu tch')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      // URLSearchParams encodes space as '+'.
      expect(url).toContain('prefix=clu+tch')
    })

    it('passes through a limit', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse([])),
      )

      await tags.autocomplete('c', 5)

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toContain('limit=5')
    })

    it('omits prefix from the query string when empty', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse([])),
      )

      await tags.autocomplete('')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      // Either '/tags' (no qs) or '/tags?' is acceptable — both ignore an empty prefix.
      expect(url.startsWith(`${BASE_URL}/tags`)).toBe(true)
      expect(url).not.toContain('prefix=')
    })
  })

  describe('getBySlug()', () => {
    it('GETs /tags/{slug} with the encoded slug', async () => {
      const payload = { id: 1, slug: 'clutch', name: 'clutch', clipCount: 5 }
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(payload)),
      )

      const result = await tags.getBySlug('clutch')

      expect(result).toEqual(payload)
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/tags/clutch`)
    })

    it('throws ApiError on 404', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ code: 'not_found' }, 404)),
      )

      await expect(tags.getBySlug('does-not-exist')).rejects.toMatchObject({ status: 404 })
    })
  })

  describe('clips()', () => {
    it('GETs /tags/{slug}/clips and parses the page', async () => {
      const payload = { items: [], nextCursor: null }
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(payload)),
      )

      const result = await tags.clips('clutch')

      expect(result).toEqual(payload)
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/tags/clutch/clips`)
    })

    it('passes cursor and limit through', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await tags.clips('clutch', { cursor: 'abc=', limit: 5 })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toContain('cursor=abc%3D')
      expect(url).toContain('limit=5')
    })
  })
})
