import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { login, me, oauthStartUrl, register, setPassword, updateMe } from '../auth'
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
    it('issues GET /auth/me against BASE_URL and returns the parsed profile', async () => {
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
      expect(url).toBe(`${BASE_URL}/auth/me`)
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

  describe('register()', () => {
    it('POSTs the credentials body to /auth/register and returns the token response', async () => {
      const tokens = { token: 'jwt', refresh: 'r1', expiresIn: 600 }
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(JSON.stringify(tokens), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            }),
        ),
      )

      const result = await register({
        email: 'a@b.dev',
        username: 'a',
        password: 'long-strong-password',
      })

      expect(result).toEqual(tokens)
      // vi.mocked(fetch) (not the local mock var) preserves the global fetch's
      // `[input, init?]` call signature so the [url, init] tuple destructure type-checks.
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/auth/register`)
      expect(init.method).toBe('POST')
      expect(JSON.parse(init.body as string)).toEqual({
        email: 'a@b.dev',
        username: 'a',
        password: 'long-strong-password',
      })
    })
  })

  describe('login()', () => {
    it('POSTs the credentials body to /auth/login and returns the token response', async () => {
      const tokens = { token: 'jwt', refresh: 'r1', expiresIn: 600 }
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(JSON.stringify(tokens), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            }),
        ),
      )

      const result = await login({ email: 'a@b.dev', password: 'long-strong-password' })

      expect(result).toEqual(tokens)
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/auth/login`)
      expect(init.method).toBe('POST')
    })
  })

  describe('updateMe()', () => {
    it('PATCHes /auth/me with the sparse payload and returns the parsed MeResponse', async () => {
      const updated = {
        id: '1',
        username: 'zoe',
        email: null,
        bio: null,
        avatarUrl: null,
        avatarSource: null,
        oauthAvatarUrl: null,
        bannerUrl: null,
        accentColor: '#6D28D9',
        socialLinks: { twitch: 'zoe', youtube: null, twitter: null },
        createdAt: '',
        hasPassword: false,
        role: 'user',
      }
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(JSON.stringify(updated), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            }),
        ),
      )

      const result = await updateMe({
        accentColor: '#6D28D9',
        socialLinks: { twitch: 'zoe' },
      })

      expect(result.accentColor).toBe('#6D28D9')
      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/auth/me`)
      expect(init.method).toBe('PATCH')
      expect(JSON.parse(init.body as string)).toEqual({
        accentColor: '#6D28D9',
        socialLinks: { twitch: 'zoe' },
      })
    })
  })

  describe('setPassword()', () => {
    it('POSTs both currentPassword and newPassword when current is provided', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(null, {
              status: 204,
              headers: { 'content-length': '0' },
            }),
        ),
      )

      await setPassword('old-password', 'new-strong-password')

      const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(url).toBe(`${BASE_URL}/auth/password`)
      expect(JSON.parse(init.body as string)).toEqual({
        currentPassword: 'old-password',
        newPassword: 'new-strong-password',
      })
    })

    it('passes currentPassword:null when caller has no existing password', async () => {
      // OAuth-only users attaching a password for the first time — server treats
      // the OAuth login that minted the token as proof of account control.
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(null, {
              status: 204,
              headers: { 'content-length': '0' },
            }),
        ),
      )

      await setPassword(null, 'new-strong-password')

      const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
      expect(JSON.parse(init.body as string)).toEqual({
        currentPassword: null,
        newPassword: 'new-strong-password',
      })
    })
  })
})
