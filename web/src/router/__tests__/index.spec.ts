import { describe, it, expect, beforeEach, beforeAll, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import type { Router } from 'vue-router'

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
vi.mock('@/views/UserView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/AuthCallbackView.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/views/NotFoundView.vue', () => ({ default: { template: '<div />' } }))

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
    await router.push('/upload?ref=abc')
    expect(router.currentRoute.value.query.redirect).toBe('/upload?ref=abc')
  })

  it('allows requiresAuth routes once the user is authenticated', async () => {
    const auth = useAuthStore()
    auth.setUser({
      id: '1',
      username: 'signed-in',
      email: null,
      bio: null,
      avatarUrl: null,
      createdAt: '',
    })

    await router.push('/upload')
    expect(router.currentRoute.value.name).toBe('upload')
  })

  it('maps unknown paths to the not-found view', async () => {
    await router.push('/something-that-does-not-exist')
    expect(router.currentRoute.value.name).toBe('not-found')
  })
})
