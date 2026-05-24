import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { report } from '../reports'
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

describe('api/reports', () => {
  it('POSTs /clips/{id}/report for clip target', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ id: 'r1' }, 201)),
    )
    const result = await report('clip', 'c1', 'spam')
    expect(result.id).toBe('r1')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/clips/c1/report`)
    expect(init.method).toBe('POST')
    expect(JSON.parse(String(init.body))).toEqual({ reason: 'spam', note: null })
  })

  it('POSTs /comments/{id}/report for comment target', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ id: 'r2' }, 201)),
    )
    await report('comment', 'c2', 'harassment', 'rude')
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    expect(url).toBe(`${BASE_URL}/comments/c2/report`)
    expect(JSON.parse(String(init.body))).toEqual({ reason: 'harassment', note: 'rude' })
  })

  it('POSTs /users/{id}/report for user target', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ id: 'r3' }, 201)),
    )
    await report('user', 'u1', 'hate')
    const [url] = vi.mocked(fetch).mock.calls[0] as [string]
    expect(url).toBe(`${BASE_URL}/users/u1/report`)
  })
})
