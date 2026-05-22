import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { search } from '../search'
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

describe('api/search', () => {
  it('GETs /search?q= with just the query', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ clips: [], games: [] })),
    )

    await search.query('valorant')

    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/search?q=valorant`)
  })

  it('URL-encodes the query', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ clips: [], games: [] })),
    )

    await search.query('valor & rl')

    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    // URLSearchParams encodes spaces as '+' and '&' as '%26'.
    expect(url).toBe(`${BASE_URL}/search?q=valor+%26+rl`)
  })

  it('appends type and limit when provided', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ clips: [], games: [] })),
    )

    await search.query('val', { type: 'games', limit: 5 })

    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/search?q=val&type=games&limit=5`)
  })

  it('omits limit when not provided', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ clips: [], games: [] })),
    )

    await search.query('val', { type: 'all' })

    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/search?q=val&type=all`)
  })

  it('returns the parsed response shape', async () => {
    const body = {
      clips: [
        {
          id: 'abc',
          title: 't',
          description: null,
          thumbnailUrl: 'https://thumb.test/x.jpg',
          durationSecs: 30,
          viewCount: 0,
          likeCount: 0,
          createdAt: new Date().toISOString(),
          author: { id: 'u', username: 'u', avatarUrl: null },
          game: null,
          likedByMe: false,
          shareCode: 'abc123',
        },
      ],
      games: [{ id: 1, name: 'Valorant', slug: 'valorant', tag: 'VAL', coverUrl: null }],
    }
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(body)),
    )

    const result = await search.query('valorant')
    expect(result).toEqual(body)
  })
})
