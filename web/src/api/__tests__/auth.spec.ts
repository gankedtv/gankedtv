import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { me, oauthStartUrl } from '../auth'
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

describe('api/auth', () => {
  describe('me()', () => {
    it('issues GET /me against BASE_URL and returns the parsed profile', async () => {
      const profile = {
        id: '1',
        username: 'zoe',
        email: 'zoe@example.com',
        bio: null,
        avatarUrl: null,
        createdAt: '2026-04-20T00:00:00Z',
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(JSON.stringify(profile), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            }),
        ),
      )

      const result = await me()

      expect(result).toEqual(profile)
      const [url] = vi.mocked(fetch).mock.calls[0] as [string]
      expect(url).toBe(`${BASE_URL}/me`)
    })
  })

  describe('oauthStartUrl()', () => {
    it('builds a discord start URL without returnTo', () => {
      expect(oauthStartUrl('discord')).toBe(`${BASE_URL}/auth/discord/start`)
    })

    it('builds a google start URL and URL-encodes returnTo', () => {
      // Encoding matters: `/clip/abc?ref=xyz` contains '?' which would otherwise split the
      // query string and be interpreted as a separate parameter by the server.
      expect(oauthStartUrl('google', '/clip/abc?ref=xyz')).toBe(
        `${BASE_URL}/auth/google/start?returnTo=${encodeURIComponent('/clip/abc?ref=xyz')}`,
      )
    })

    it('treats an empty returnTo as absent (no query string)', () => {
      // Empty string is falsy — the guard skips the returnTo path, otherwise we'd produce
      // the ugly `.../start?returnTo=` which some gateways reject as a malformed query.
      expect(oauthStartUrl('discord', '')).toBe(`${BASE_URL}/auth/discord/start`)
    })
  })
})
