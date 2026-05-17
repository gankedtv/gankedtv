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

  describe('getBySlug()', () => {
    it('GETs /games/{slug} with the slug URL-encoded', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () =>
          jsonResponse({
            id: 1,
            name: 'Valorant',
            slug: 'valorant',
            tag: 'VALORANT',
            coverUrl: null,
            clipCount: 0,
          }),
        ),
      )

      await games.getBySlug('rocket league')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/games/rocket%20league`)
    })

    it('returns the parsed detail', async () => {
      const body = {
        id: 7,
        name: 'Rocket League',
        slug: 'rocket-league',
        tag: 'RL',
        coverUrl: 'https://cdn.test/rl.jpg',
        clipCount: 42,
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(body)),
      )

      const result = await games.getBySlug('rocket-league')

      expect(result).toEqual(body)
    })
  })

  describe('clips()', () => {
    it('GETs /games/{slug}/clips with no query when no cursor/limit', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await games.clips('valorant')

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/games/valorant/clips`)
    })

    it('passes cursor and limit through as query params', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await games.clips('valorant', { cursor: 'abc=', limit: 20 })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      // URLSearchParams encodes '=' as '%3D'.
      expect(url).toBe(`${BASE_URL}/games/valorant/clips?cursor=abc%3D&limit=20`)
    })

    it('skips cursor when null', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ items: [], nextCursor: null })),
      )

      await games.clips('valorant', { cursor: null, limit: 10 })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/games/valorant/clips?limit=10`)
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
