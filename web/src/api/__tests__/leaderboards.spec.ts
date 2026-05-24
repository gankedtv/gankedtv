import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { leaderboards } from '../leaderboards'
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

describe('api/leaderboards', () => {
  describe('global()', () => {
    it('GETs /leaderboards with the requested window', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ window: 'week', topClips: [], topGames: [] })),
      )

      await leaderboards.global({ window: 'week' })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/leaderboards?window=week`)
    })

    it('appends clipsLimit and gamesLimit when provided', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ window: 'month', topClips: [], topGames: [] })),
      )

      await leaderboards.global({ window: 'month', clipsLimit: 20, gamesLimit: 5 })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/leaderboards?window=month&clipsLimit=20&gamesLimit=5`)
    })

    it('omits limit params when not provided', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse({ window: 'all', topClips: [], topGames: [] })),
      )

      await leaderboards.global({ window: 'all' })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/leaderboards?window=all`)
    })

    it('returns the parsed body', async () => {
      const body = {
        window: 'week',
        topClips: [],
        topGames: [
          {
            rank: 1,
            windowLikes: 5,
            clipCount: 3,
            game: { id: 1, name: 'Valorant', slug: 'valorant', tag: 'VAL' },
            coverUrl: null,
          },
        ],
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(body)),
      )

      const result = await leaderboards.global({ window: 'week' })

      expect(result).toEqual(body)
    })
  })

  describe('forGame()', () => {
    it('GETs /games/{slug}/leaderboard with the window', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () =>
          jsonResponse({
            window: 'week',
            game: { id: 1, name: 'Valorant', slug: 'valorant', tag: 'VAL' },
            entries: [],
          }),
        ),
      )

      await leaderboards.forGame('valorant', { window: 'week' })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/games/valorant/leaderboard?window=week`)
    })

    it('URL-encodes the slug', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () =>
          jsonResponse({
            window: 'week',
            game: { id: 1, name: 'Rocket League', slug: 'rocket league', tag: 'RL' },
            entries: [],
          }),
        ),
      )

      await leaderboards.forGame('rocket league', { window: 'week' })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/games/rocket%20league/leaderboard?window=week`)
    })

    it('appends limit when provided', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () =>
          jsonResponse({
            window: 'all',
            game: { id: 1, name: 'Valorant', slug: 'valorant', tag: 'VAL' },
            entries: [],
          }),
        ),
      )

      await leaderboards.forGame('valorant', { window: 'all', limit: 25 })

      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/games/valorant/leaderboard?window=all&limit=25`)
    })
  })
})
