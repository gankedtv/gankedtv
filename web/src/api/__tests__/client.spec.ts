import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { api, ApiError, configureAuth } from '../client'

const mockLogout = vi.fn()
const mockOnTokenRefreshed = vi.fn()
let mockAccessToken: string | null = null
let mockRefreshToken: string | null = null

function setupAuth(opts: { accessToken?: string | null; refreshToken?: string | null } = {}) {
  mockAccessToken = opts.accessToken ?? null
  mockRefreshToken = opts.refreshToken ?? null
  configureAuth({
    getAccessToken: () => mockAccessToken,
    getRefreshToken: () => mockRefreshToken,
    onTokenRefreshed: mockOnTokenRefreshed,
    onRefreshFailed: mockLogout,
  })
}

function mockFetch(responses: { status: number; body?: unknown }[]) {
  let call = 0
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => {
      const r = responses[call++] ?? responses[responses.length - 1]!
      const body = r.body !== undefined ? JSON.stringify(r.body) : ''
      return new Response(body, {
        status: r.status,
        headers: { 'content-type': r.body !== undefined ? 'application/json' : 'text/plain' },
      })
    }),
  )
}

beforeEach(() => {
  setupAuth()
  mockLogout.mockClear()
  mockOnTokenRefreshed.mockClear()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('api client', () => {
  it('sends bearer header when accessToken is set', async () => {
    setupAuth({ accessToken: 'abc123' })
    mockFetch([{ status: 200, body: { ok: true } }])

    await api('/test')

    const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    const headers = new Headers(init.headers)
    expect(headers.get('Authorization')).toBe('Bearer abc123')
  })

  it('does not send bearer when no accessToken', async () => {
    mockFetch([{ status: 200, body: {} }])
    await api('/test')
    const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    const headers = new Headers(init.headers)
    expect(headers.get('Authorization')).toBeNull()
  })

  it('retries once on 401 using refresh token', async () => {
    setupAuth({ accessToken: 'stale', refreshToken: 'valid-refresh' })
    // refresh endpoint returns new tokens
    mockFetch([
      { status: 401 },
      { status: 200, body: { token: 'new-access', refresh: 'new-refresh', expiresIn: 900 } },
      { status: 200, body: { data: 'ok' } },
    ])

    const result = await api<{ data: string }>('/protected')

    expect(mockOnTokenRefreshed).toHaveBeenCalledWith('new-access', 'new-refresh')
    expect(result).toEqual({ data: 'ok' })
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(3)
  })

  it('calls onRefreshFailed and throws when refresh itself fails', async () => {
    setupAuth({ refreshToken: 'bad-refresh' })
    mockFetch([
      { status: 401 },
      { status: 401 }, // refresh endpoint also 401
    ])

    await expect(api('/protected')).rejects.toBeInstanceOf(ApiError)
    expect(mockLogout).toHaveBeenCalled()
  })

  it('does not retry when _isRetry is set', async () => {
    setupAuth({ refreshToken: 'valid' })
    mockFetch([{ status: 401 }])

    await expect(api('/protected', { _isRetry: true } as never)).rejects.toBeInstanceOf(ApiError)
    // fetch called only once — no refresh attempt
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1)
  })

  it('concurrent 401s share a single refresh call', async () => {
    setupAuth({ refreshToken: 'valid' })
    let refreshCalls = 0

    const retried = new Set<string>()
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string | URL | Request) => {
        const url = String(input instanceof Request ? input.url : input)
        if (url.includes('/auth/refresh')) {
          refreshCalls++
          return new Response(JSON.stringify({ token: 'new', refresh: 'new-r', expiresIn: 900 }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
        const path = new URL(url).pathname
        if (retried.has(path)) {
          return new Response('{}', {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
        retried.add(path)
        return new Response('{}', { status: 401, headers: { 'content-type': 'application/json' } })
      }),
    )

    await Promise.all([api('/a'), api('/b')])
    expect(refreshCalls).toBe(1)
  })

  it('throws ApiError on non-2xx non-401', async () => {
    mockFetch([{ status: 403, body: { error: 'forbidden' } }])
    await expect(api('/secret')).rejects.toBeInstanceOf(ApiError)
  })
})
