import { describe, it, expect, vi, beforeEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { ApiError } from '@/api/client'
import { createLocalStorageMock, type MockLocalStorage } from '@/test/helpers'
import { useAuthStore } from '../auth'

const mockMe = vi.fn()

vi.mock('@/api/auth', () => ({
  me: () => mockMe(),
}))

vi.mock('@/router', () => ({
  default: { push: vi.fn(), isReady: vi.fn(() => Promise.resolve()) },
}))

// Recreated in beforeEach so per-test overrides (throw-mode, method swaps) don't bleed
// into the next test. Defined via a getter on window.localStorage so reassigning the outer
// `localStorageMock` transparently rotates the backing store — no second Object.defineProperty
// call needed per test.
let localStorageMock: MockLocalStorage = createLocalStorageMock()
Object.defineProperty(window, 'localStorage', {
  configurable: true,
  get: () => localStorageMock,
})

beforeEach(() => {
  localStorageMock = createLocalStorageMock()
  setActivePinia(createPinia())
  mockMe.mockClear()
})

describe('useAuthStore', () => {
  it('starts unauthenticated', () => {
    const auth = useAuthStore()
    expect(auth.isAuthenticated).toBe(false)
    expect(auth.user).toBeNull()
    expect(auth.accessToken).toBeNull()
  })

  it('setSession stores token and persists refresh to localStorage', () => {
    const auth = useAuthStore()
    auth.setSession('tok', 'ref')
    expect(auth.accessToken).toBe('tok')
    expect(auth.refreshToken).toBe('ref')
    expect(localStorageMock.getItem('refresh_token')).toBe('ref')
  })

  it('bootstrap with persisted refresh token fetches user', async () => {
    localStorageMock.setItem('refresh_token', 'valid-ref')
    mockMe.mockResolvedValueOnce({
      id: '1',
      username: 'player1',
      email: null,
      bio: null,
      avatarUrl: null,
      avatarSource: null,
      oauthAvatarUrl: null,
      bannerUrl: null,
      accentColor: null,
      socialLinks: null,
      createdAt: '',
      hasPassword: false,
    })

    const auth = useAuthStore()
    await auth.bootstrap()

    expect(auth.isAuthenticated).toBe(true)
    expect(auth.user?.username).toBe('player1')
  })

  it('bootstrap clears state silently when fetchMe fails', async () => {
    localStorageMock.setItem('refresh_token', 'bad-ref')
    mockMe.mockRejectedValueOnce(new ApiError(401, null))

    const auth = useAuthStore()
    await auth.bootstrap()

    expect(auth.isAuthenticated).toBe(false)
    expect(auth.refreshToken).toBeNull()
    expect(localStorageMock.getItem('refresh_token')).toBeNull()
  })

  it('bootstrap is idempotent', async () => {
    const auth = useAuthStore()
    await auth.bootstrap()
    await auth.bootstrap()
    expect(mockMe).not.toHaveBeenCalled()
  })

  it('logout clears state and removes localStorage key', () => {
    localStorageMock.setItem('refresh_token', 'ref')
    const auth = useAuthStore()
    auth.setSession('tok', 'ref')
    auth.logout()

    expect(auth.isAuthenticated).toBe(false)
    expect(auth.accessToken).toBeNull()
    expect(auth.refreshToken).toBeNull()
    expect(localStorageMock.getItem('refresh_token')).toBeNull()
  })

  it('setUser sets the profile without touching session tokens or localStorage', () => {
    const auth = useAuthStore()
    auth.setSession('tok', 'ref')
    auth.setUser({
      id: '1',
      username: 'zoe',
      email: 'zoe@example.com',
      bio: null,
      avatarUrl: null,
      avatarSource: null,
      oauthAvatarUrl: null,
      bannerUrl: null,
      accentColor: null,
      socialLinks: null,
      createdAt: '',
      hasPassword: true,
      role: 'user',
    })

    expect(auth.user?.username).toBe('zoe')
    expect(auth.accessToken).toBe('tok')
    expect(auth.refreshToken).toBe('ref')
    // setUser must not re-persist: the session key already lives in localStorage via setSession.
    // A regression where setUser wrote to localStorage could mask a missing setSession call.
    expect(localStorageMock.getItem('refresh_token')).toBe('ref')
  })

  it('bootstrap rethrows non-401 ApiErrors', async () => {
    localStorageMock.setItem('refresh_token', 'ref')
    // Network-ish errors (5xx, transport failures) shouldn't silently sign the user out —
    // that would hide backend incidents from operators. Only 401 triggers the clean-state path.
    mockMe.mockRejectedValueOnce(new ApiError(500, null))

    const auth = useAuthStore()
    await expect(auth.bootstrap()).rejects.toBeInstanceOf(ApiError)

    // Refresh token must stay intact so a subsequent retry can succeed.
    expect(auth.refreshToken).toBe('ref')
    expect(localStorageMock.getItem('refresh_token')).toBe('ref')
  })

  it('bootstrap rethrows non-ApiError exceptions unchanged', async () => {
    localStorageMock.setItem('refresh_token', 'ref')
    const boom = new Error('network down')
    mockMe.mockRejectedValueOnce(boom)

    const auth = useAuthStore()
    await expect(auth.bootstrap()).rejects.toBe(boom)
  })

  it('setSession swallows localStorage.setItem failures', () => {
    const auth = useAuthStore()
    localStorageMock.setItem = () => {
      throw new Error('denied')
    }
    // Persisting the refresh token is best-effort; an in-memory session should still be
    // valid even when Safari Private Mode refuses to persist.
    expect(() => auth.setSession('tok', 'ref')).not.toThrow()
    expect(auth.accessToken).toBe('tok')
  })

  it('loadRefreshFromLocalStorage returns null when localStorage.getItem throws', async () => {
    localStorageMock.getItem = () => {
      throw new Error('denied')
    }
    // The store's state initialiser runs at first useAuthStore() call — reset the module so
    // we re-execute with the throwing mock in place.
    vi.resetModules()
    const { useAuthStore: useFresh } = await import('../auth')
    setActivePinia(createPinia())
    const auth = useFresh()

    expect(auth.refreshToken).toBeNull()
  })

  it('setSession with VITE_USE_SECURE_COOKIES=true skips localStorage persistence', async () => {
    vi.stubEnv('VITE_USE_SECURE_COOKIES', 'true')
    try {
      vi.resetModules()
      const { useAuthStore: useFresh } = await import('../auth')
      setActivePinia(createPinia())
      const auth = useFresh()

      localStorageMock.clear()
      auth.setSession('tok', 'ref')

      expect(auth.refreshToken).toBe('ref')
      // Persistence path is expected to be a no-op — the backend issues the refresh cookie.
      expect(localStorageMock.getItem('refresh_token')).toBeNull()
    } finally {
      // Always unstub, even if an assertion threw — otherwise subsequent tests inherit the
      // VITE_USE_SECURE_COOKIES=true env and start misbehaving mysteriously.
      vi.unstubAllEnvs()
    }
  })

  it('logout with VITE_USE_SECURE_COOKIES=true still clears any stale localStorage entry', async () => {
    vi.stubEnv('VITE_USE_SECURE_COOKIES', 'true')
    try {
      vi.resetModules()
      const { useAuthStore: useFresh } = await import('../auth')
      setActivePinia(createPinia())
      const auth = useFresh()

      // Simulate a migration from pre-secure-cookie mode that left a token behind; the secure
      // cookie persist path still needs to evict stale entries on logout (token === null).
      localStorageMock.setItem('refresh_token', 'stale')
      auth.logout()

      expect(localStorageMock.getItem('refresh_token')).toBeNull()
    } finally {
      vi.unstubAllEnvs()
    }
  })

  it('logout swallows router navigation failures', async () => {
    const auth = useAuthStore()
    auth.setSession('tok', 'ref')

    // Earlier tests (`logout clears state…`, `logout with VITE_USE_SECURE_COOKIES=true…`)
    // fire `auth.logout()` without awaiting — the dynamic `import('@/router').then(isReady)`
    // chain is still pending when this test starts. If we swap isReady BEFORE those
    // microtasks drain, the stale logout(s) hit the rejecting mock and pollute errSpy.
    // Flush pending microtasks + a macrotask tick so any in-flight chain settles against
    // the original (resolving) isReady first.
    await new Promise((r) => setTimeout(r, 0))

    // Reconfigure the hoisted router mock for this test only: isReady() rejects so the
    // dynamic import chain inside logout() hits the .catch. `vi.doMock` would be ignored
    // here because the hoisted `vi.mock('@/router', ...)` at module top has already been
    // resolved; reusing the existing vi.fn is what actually works.
    const routerMod = (await import('@/router')) as unknown as {
      default: { isReady: ReturnType<typeof vi.fn> }
    }
    const originalIsReady = routerMod.default.isReady
    routerMod.default.isReady = vi.fn(() => Promise.reject(new Error('router not ready')))

    const errSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    try {
      auth.logout()
      // Two microtask flushes: one for the dynamic import, one for the rejected isReady.
      await Promise.resolve()
      await Promise.resolve()
      await new Promise((r) => setTimeout(r, 0))

      expect(errSpy).toHaveBeenCalledWith('logout navigation failed', expect.any(Error))
    } finally {
      errSpy.mockRestore()
      routerMod.default.isReady = originalIsReady
    }
  })

  it('isAdmin / isModerator track the user role claim', () => {
    const auth = useAuthStore()
    expect(auth.isAdmin).toBe(false)
    expect(auth.isModerator).toBe(false)

    const base = {
      id: '1',
      username: 'u',
      email: null,
      bio: null,
      avatarUrl: null,
      avatarSource: null,
      oauthAvatarUrl: null,
      bannerUrl: null,
      accentColor: null,
      socialLinks: null,
      createdAt: '',
      hasPassword: false,
    } as const

    auth.setUser({ ...base, role: 'user' })
    expect(auth.isAdmin).toBe(false)
    expect(auth.isModerator).toBe(false)

    auth.setUser({ ...base, role: 'moderator' })
    expect(auth.isAdmin).toBe(false)
    expect(auth.isModerator).toBe(true)

    // Admin is a superset of moderator — both getters return true.
    auth.setUser({ ...base, role: 'admin' })
    expect(auth.isAdmin).toBe(true)
    expect(auth.isModerator).toBe(true)
  })
})
