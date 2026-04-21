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

  it('returns undefined on 204 No Content', async () => {
    // 204 has no body by definition — a naive res.json() would throw. The client
    // short-circuits on status===204 before reading the body.
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(null, { status: 204, headers: { 'content-type': 'application/json' } }),
      ),
    )

    const result = await api('/delete')
    expect(result).toBeUndefined()
  })

  it('returns undefined when content-length is 0', async () => {
    // Same semantic as 204 — the server explicitly advertises an empty body.
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(null, {
            status: 200,
            headers: { 'content-type': 'application/json', 'content-length': '0' },
          }),
      ),
    )
    expect(await api('/empty')).toBeUndefined()
  })

  it('parses non-JSON successful responses as text', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response('plain string body', {
            status: 200,
            headers: { 'content-type': 'text/plain' },
          }),
      ),
    )
    const result = await api<string>('/text')
    expect(result).toBe('plain string body')
  })

  it('parses error responses with no content-type as text inside ApiError.body', async () => {
    // Reverse-proxy error pages (nginx/cloud WAF) often lack a content-type; swallowing them
    // as JSON would throw and hide the real failure. Text fallback keeps the body accessible.
    // Note: passing a string body to `new Response(...)` would auto-set Content-Type:
    // text/plain, which would hit the non-JSON branch but not the `?? ''` null-coalesce in
    // client.ts. A Uint8Array body has no implicit Content-Type, so this actually exercises
    // the "header is absent" path.
    const bytes = new TextEncoder().encode('gateway down')
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(bytes, { status: 502 })),
    )

    const err = await api('/thing').catch((e: ApiError) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(502)
    expect((err as ApiError).body).toBe('gateway down')
  })

  it("rejects retry for ReadableStream bodies (they can't be replayed)", async () => {
    setupAuth({ refreshToken: 'valid' })
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(null, {
            status: 401,
            headers: { 'content-type': 'application/json' },
          }),
      ),
    )

    const stream = new ReadableStream()
    await expect(
      api('/stream', { method: 'POST', body: stream } as RequestInit),
    ).rejects.toBeInstanceOf(ApiError)
    // Exactly one fetch call: the retry path is skipped because the body cannot be replayed.
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1)
  })

  it('does not set Content-Type when body is FormData', async () => {
    // FormData must keep the browser's multipart/form-data boundary header. Forcing JSON
    // would corrupt the request.
    mockFetch([{ status: 200, body: { ok: true } }])
    const fd = new FormData()
    fd.append('x', 'y')

    await api('/upload', { method: 'POST', body: fd } as RequestInit)

    const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit]
    const headers = new Headers(init.headers)
    expect(headers.get('Content-Type')).toBeNull()
  })

  it('retries without a refresh token skips refresh and throws', async () => {
    // No refresh token available — the 401 check short-circuits the refresh attempt. This
    // is the "user is anonymous, got a 401 from a protected endpoint" path.
    mockFetch([{ status: 401 }])

    await expect(api('/protected')).rejects.toBeInstanceOf(ApiError)
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1)
    expect(mockLogout).not.toHaveBeenCalled()
  })

  it('refresh fetch failure (no body) surfaces ApiError and triggers onRefreshFailed', async () => {
    setupAuth({ refreshToken: 'ref' })
    mockFetch([
      { status: 401 },
      { status: 500 }, // refresh endpoint 500 with no body
    ])

    await expect(api('/thing')).rejects.toBeInstanceOf(ApiError)
    expect(mockLogout).toHaveBeenCalled()
  })

  it('throws on refresh when refresh token rotates mid-flight', async () => {
    // User called logout while a refresh was in flight; the guard inside runRefresh
    // detects the change and bails rather than stomping a session that no longer exists.
    mockAccessToken = null
    mockRefreshToken = 'initial'
    configureAuth({
      getAccessToken: () => mockAccessToken,
      getRefreshToken: () => mockRefreshToken,
      onTokenRefreshed: mockOnTokenRefreshed,
      onRefreshFailed: mockLogout,
    })

    let callCount = 0
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string | URL | Request) => {
        const url = String(input instanceof Request ? input.url : input)
        callCount++
        if (url.includes('/auth/refresh')) {
          // Simulate concurrent logout flipping the refresh token mid-flight.
          mockRefreshToken = null
          return new Response(JSON.stringify({ token: 'new', refresh: 'new-r', expiresIn: 900 }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
        return new Response('{}', {
          status: 401,
          headers: { 'content-type': 'application/json' },
        })
      }),
    )

    await expect(api('/thing')).rejects.toBeInstanceOf(ApiError)
    expect(mockOnTokenRefreshed).not.toHaveBeenCalled()
    expect(mockLogout).toHaveBeenCalled()
    expect(callCount).toBeGreaterThanOrEqual(2)
  })
})
