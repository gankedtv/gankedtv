import { describe, it, expect, beforeEach, beforeAll, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import { trackPageView } from '@/lib/analytics'
import type { Router } from 'vue-router'

// Keep the real stripSensitiveParams (so the redaction is exercised end-to-end) but spy on
// the sink so we can assert exactly what would be shipped to analytics.
vi.mock('@/lib/analytics', async () => {
  const actual = await vi.importActual<typeof import('@/lib/analytics')>('@/lib/analytics')
  return { ...actual, trackPageView: vi.fn() }
})

// Swap createWebHistory → createMemoryHistory in the router module so navigation doesn't
// depend on the jsdom window.location. This lets router.isReady() resolve deterministically
// without touching the DOM.
vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')
  return { ...actual, createWebHistory: actual.createMemoryHistory }
})

vi.mock('@/views/HomeView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/LoginView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/UploadView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/ClipView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/ReelsView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/UserView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/AuthCallbackView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/NotFoundView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/AdminView.vue', () => ({ default: { template: '<div />' } }))

let router: Router

beforeAll(async () => {
  // Activate pinia before the first useAuthStore() lookup inside the guard. Later
  // beforeEach calls rotate to a fresh pinia so tests don't leak auth state into each other,
  // but we still need SOME pinia active when isReady() triggers the initial navigation.
  setActivePinia(createPinia())
  const mod = await import('../index')
  router = mod.default
  // Memory history has no initial URL, so kick off the first navigation manually before
  // awaiting isReady(); otherwise isReady() never resolves.
  await router.push('/')
})

beforeEach(async () => {
  setActivePinia(createPinia())
  // Reset the current route between tests so push('/upload') after a successful navigation
  // to /upload in a previous test is still observable as a navigation event.
  await router.replace('/')
})

describe('router beforeEach guard', () => {
  it('passes through public routes when unauthenticated', async () => {
    await router.push('/')
    expect(router.currentRoute.value.name).toBe('home')
  })

  it('redirects to login with ?redirect=… when hitting a requiresAuth route unauthenticated', async () => {
    await router.push('/upload')
    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/upload')
  })

  it('preserves query and hash in the redirect target (fullPath)', async () => {
    // `to.fullPath` — not `to.path` — is what the guard passes through. Push both a query
    // string AND a hash so a regression that dropped either would surface here.
    await router.push('/upload?ref=abc#section')
    expect(router.currentRoute.value.query.redirect).toBe('/upload?ref=abc#section')
  })

  it('allows requiresAuth routes once the user is authenticated', async () => {
    const auth = useAuthStore()
    auth.setUser({
      id: '1',
      username: 'signed-in',
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
      role: 'user',
    })

    await router.push('/upload')
    expect(router.currentRoute.value.name).toBe('upload')
  })

  it('maps unknown paths to the not-found view', async () => {
    await router.push('/something-that-does-not-exist')
    expect(router.currentRoute.value.name).toBe('not-found')
  })

  it('redirects non-moderators away from /admin to home', async () => {
    const auth = useAuthStore()
    auth.setUser({
      id: '1',
      username: 'plain',
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
      role: 'user',
    })
    await router.push('/admin')
    expect(router.currentRoute.value.name).toBe('home')
  })

  it('allows moderators onto /admin', async () => {
    const auth = useAuthStore()
    auth.setUser({
      id: '1',
      username: 'mod',
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
      role: 'moderator',
    })
    await router.push('/admin')
    expect(router.currentRoute.value.name).toBe('admin')
  })

  it('allows admins onto /admin (superset of moderator)', async () => {
    const auth = useAuthStore()
    auth.setUser({
      id: '1',
      username: 'admin',
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
      role: 'admin',
    })
    await router.push('/admin')
    expect(router.currentRoute.value.name).toBe('admin')
  })
})

describe('analytics page-view hook', () => {
  it('emits a page view with the full path after each navigation', async () => {
    vi.mocked(trackPageView).mockClear()
    await router.push('/clip/clp_7?ref=feed')
    expect(trackPageView).toHaveBeenCalledWith('/clip/clp_7?ref=feed')
  })

  it('strips OAuth credentials from the tracked path on /auth/callback', async () => {
    vi.mocked(trackPageView).mockClear()
    await router.push('/auth/callback?token=fake-jwt&refresh=fake-refresh&returnTo=/feed')

    expect(trackPageView).toHaveBeenCalledTimes(1)
    const tracked = vi.mocked(trackPageView).mock.calls[0][0]
    expect(tracked).not.toContain('token')
    expect(tracked).not.toContain('fake-jwt')
    expect(tracked).not.toContain('refresh')
    expect(tracked).not.toContain('fake-refresh')
    // Non-sensitive params are preserved.
    expect(tracked).toContain('returnTo=%2Ffeed')
  })
})

describe('share-code route', () => {
  it('resolves /c/:code to the clip-share route and exposes the code param', async () => {
    await router.push('/c/abc123')
    expect(router.currentRoute.value.name).toBe('clip-share')
    expect(router.currentRoute.value.params.code).toBe('abc123')
  })
})

describe('reels routes', () => {
  it('resolves /feed/reels to the bare reels route with no id param', async () => {
    await router.push('/feed/reels')
    expect(router.currentRoute.value.name).toBe('reels')
    expect(router.currentRoute.value.params.id).toBeUndefined()
  })

  it('resolves /feed/reels/:id to the reel-clip route and exposes the id param', async () => {
    await router.push('/feed/reels/clp_42')
    expect(router.currentRoute.value.name).toBe('reel-clip')
    expect(router.currentRoute.value.params.id).toBe('clp_42')
  })
})
