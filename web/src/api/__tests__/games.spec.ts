import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { games } from '../games'
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

describe('api/games', () => {
  describe('list()', () => {
    it('GETs /games without query when no limit', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse([])),
      )

      await games.list()

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/games`)
    })

    it('appends ?limit= when provided', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse([])),
      )

      await games.list(6)

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/games?limit=6`)
    })

    it('returns the parsed list', async () => {
      const body = [{ id: 1, name: 'Valorant', slug: 'valorant', tag: 'VALORANT', coverUrl: null }]
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(body)),
      )

      const result = await games.list()

      expect(result).toEqual(body)
    })
  })

  describe('search()', () => {
    it('URL-encodes the search term', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse([])),
      )

      await games.search('valor & rl')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      // URLSearchParams encodes spaces as '+' and '&' as '%26'.
      expect(url).toBe(`${BASE_URL}/games?search=valor+%26+rl`)
    })

    it('appends limit when provided', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse([])),
      )

      await games.search('val', 3)

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toContain('search=val')
      expect(url).toContain('limit=3')
    })
  })
})
