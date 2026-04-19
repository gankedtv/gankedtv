import { describe, it, expect, vi, beforeEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useAuthStore } from '../auth'

const mockMe = vi.fn()

vi.mock('@/api/auth', () => ({
  me: () => mockMe(),
}))

vi.mock('@/router', () => ({
  default: { push: vi.fn() },
}))

const localStorageMock = (() => {
  let store: Record<string, string> = {}
  return {
    getItem: (k: string) => store[k] ?? null,
    setItem: (k: string, v: string) => {
      store[k] = v
    },
    removeItem: (k: string) => {
      delete store[k]
    },
    clear: () => {
      store = {}
    },
  }
})()

Object.defineProperty(window, 'localStorage', { value: localStorageMock })

beforeEach(() => {
  localStorageMock.clear()
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
      createdAt: '',
    })

    const auth = useAuthStore()
    await auth.bootstrap()

    expect(auth.isAuthenticated).toBe(true)
    expect(auth.user?.username).toBe('player1')
  })

  it('bootstrap clears state silently when fetchMe fails', async () => {
    localStorageMock.setItem('refresh_token', 'bad-ref')
    mockMe.mockRejectedValueOnce(new Error('401'))

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
})
