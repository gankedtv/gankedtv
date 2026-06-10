import { config } from '@/config'

const BASE_URL = config.apiBaseUrl

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: unknown,
  ) {
    super(`API error ${status}`)
  }
}

interface AuthCallbacks {
  getAccessToken(): string | null
  getRefreshToken(): string | null
  onTokenRefreshed(token: string, refresh: string): void
  onRefreshFailed(): void
}

// Default no-op — replaced by configureAuth() in main.ts after stores are wired up
let _auth: AuthCallbacks = {
  getAccessToken: () => null,
  getRefreshToken: () => null,
  onTokenRefreshed: () => {},
  onRefreshFailed: () => {},
}

export function configureAuth(callbacks: AuthCallbacks): void {
  _auth = callbacks
}

// Single in-flight refresh promise shared across concurrent 401s
let refreshing: Promise<void> | null = null

// Monotonic session identity. The auth store bumps it on every setSession/logout so a
// refresh that was in flight for a PREVIOUS session is discarded without touching the
// current one — comparing refresh-token strings can't catch a logout-then-login switch,
// and cookie mode has no client-visible token to compare at all.
let sessionEpoch = 0

export function bumpSessionEpoch(): void {
  sessionEpoch++
}

async function runRefresh(): Promise<void> {
  const capturedEpoch = sessionEpoch
  const useCookies = config.useSecureCookies
  const capturedRefreshToken = useCookies ? null : _auth.getRefreshToken()
  if (!useCookies && !capturedRefreshToken) {
    _auth.onRefreshFailed()
    throw new ApiError(401, null)
  }
  // Cookie mode posts an empty object — the server reads the HttpOnly cookie, which only
  // flows cross-origin with credentials: 'include'.
  const res = await fetch(`${BASE_URL}/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(useCookies ? {} : { refresh: capturedRefreshToken }),
    ...(useCookies ? { credentials: 'include' as const } : {}),
  })
  if (sessionEpoch !== capturedEpoch) {
    // A different session took over mid-flight; fail this caller without clobbering it.
    throw new ApiError(401, null)
  }
  if (!res.ok) {
    _auth.onRefreshFailed()
    throw new ApiError(res.status, null)
  }
  const data = (await res.json()) as { token: string; refresh: string }
  if (sessionEpoch !== capturedEpoch) {
    throw new ApiError(401, null)
  }
  if (!useCookies) {
    // Plain logout clears the token without necessarily bumping the epoch from this
    // module's perspective (callbacks may be wired before the store exists) — keep the
    // token-identity check as a second line of defense in localStorage mode.
    const currentRefreshToken = _auth.getRefreshToken()
    if (!currentRefreshToken || currentRefreshToken !== capturedRefreshToken) {
      _auth.onRefreshFailed()
      throw new ApiError(401, null)
    }
  }
  _auth.onTokenRefreshed(data.token, data.refresh ?? '')
}

function refreshTokensOnce(): Promise<void> {
  if (!refreshing) {
    refreshing = runRefresh().finally(() => {
      refreshing = null
    })
  }
  return refreshing
}

function buildHeaders(accessToken: string | null, extra?: HeadersInit): Headers {
  const headers = new Headers(extra)
  headers.set('Accept', 'application/json')
  if (accessToken) {
    headers.set('Authorization', `Bearer ${accessToken}`)
  }
  return headers
}

export type ApiInit = Omit<RequestInit, 'body'> & {
  body?: BodyInit | object | null
  _isRetry?: boolean
}

export async function api<T = undefined>(path: string, init: ApiInit = {}): Promise<T> {
  const { _isRetry, ...fetchInit } = init

  const headers = buildHeaders(_auth.getAccessToken(), fetchInit.headers)

  if (
    fetchInit.body !== null &&
    fetchInit.body !== undefined &&
    (Object.prototype.toString.call(fetchInit.body) === '[object Object]' ||
      Array.isArray(fetchInit.body)) &&
    !(fetchInit.body instanceof FormData) &&
    !(fetchInit.body instanceof URLSearchParams)
  ) {
    if (!headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json')
    }
    fetchInit.body = JSON.stringify(fetchInit.body)
  }

  const response = await fetch(`${BASE_URL}${path}`, {
    ...fetchInit,
    headers,
    body: fetchInit.body as BodyInit | null | undefined,
  })

  // Cookie mode can always attempt a refresh — the credential is server-side.
  if (
    response.status === 401 &&
    !_isRetry &&
    (_auth.getRefreshToken() || config.useSecureCookies)
  ) {
    // Streaming request bodies cannot be replayed on retry.
    if (init.body instanceof ReadableStream) {
      throw new ApiError(401, null)
    }
    try {
      await refreshTokensOnce()
      return api<T>(path, { ...init, _isRetry: true })
    } catch (err) {
      // runRefresh already called onRefreshFailed — just re-throw
      throw err instanceof ApiError ? err : new ApiError(401, null)
    }
  }

  const hasBody = response.status !== 204 && response.headers.get('content-length') !== '0'

  let body: unknown
  if (hasBody) {
    const contentType = response.headers.get('content-type') ?? ''
    if (contentType.includes('application/json')) {
      body = await response.json()
    } else {
      body = await response.text()
    }
  }

  if (!response.ok) {
    throw new ApiError(response.status, body ?? null)
  }

  if (!hasBody) {
    return undefined as T
  }

  return body as T
}

export { BASE_URL }
