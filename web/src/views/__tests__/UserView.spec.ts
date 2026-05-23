import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory, type Router } from 'vue-router'
import { createPinia } from 'pinia'
import { defineComponent, h } from 'vue'

// Mock the profile API so the component can mount without a backend.
const getByUsername = vi.fn()
vi.mock('@/api/users', () => ({ users: { getByUsername: (u: string) => getByUsername(u) } }))

// Stub the follows API — UserView imports it for the follow/unfollow button but the
// tests below don't exercise that path (no auth.user, so the button is hidden).
vi.mock('@/api/follows', () => ({ follows: { follow: vi.fn(), unfollow: vi.fn() } }))

import UserView from '../UserView.vue'

function makeRouter(): Router {
  const stub = defineComponent({ render: () => h('div') })
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: stub },
      { path: '/user/:username', name: 'user', component: UserView },
      // Registered so RouterLink resolutions inside UserView's stat cells don't
      // emit "No match for route" warnings when followerCount/followingCount > 0
      // in fixtures that exercise the linked path.
      { path: '/user/:username/followers', name: 'user-followers', component: stub },
      { path: '/user/:username/following', name: 'user-following', component: stub },
      { path: '/clip/:id', name: 'clip', component: stub },
      { path: '/:pathMatch(.*)*', name: 'not-found', component: stub },
    ],
  })
}

describe('UserView (issue #92 regression)', () => {
  beforeEach(() => {
    getByUsername.mockReset()
  })

  // The route-level <Transition mode="out-in"> in App.vue can only animate a component
  // with a SINGLE root *element*. A stray comment node before the root element makes the
  // component multi-root (comment + element), which Transition can't drive — its leave
  // never resolves and the NEXT route's view never mounts (issue #92, bug #2). This guards
  // that UserView keeps a single element root regardless of load state.
  it('renders a single element root, not a leading-comment fragment', async () => {
    getByUsername.mockReturnValue(new Promise(() => {})) // never resolves: stay in loading state
    const router = makeRouter()
    await router.push({ name: 'user', params: { username: 'seeduser' } })
    await router.isReady()
    const wrapper = mount(UserView, { global: { plugins: [router, createPinia()] } })

    // A comment placed before the root element makes the component multi-root, so the
    // rendered output begins with the comment node. <Transition> can't drive that and its
    // leave never resolves (issue #92). Assert the root is the element, not a comment.
    expect(wrapper.html().trimStart().startsWith('<!--')).toBe(false)
    expect(wrapper.html().trimStart().startsWith('<div')).toBe(true)
  })

  it('renders profile clips once loaded', async () => {
    getByUsername.mockResolvedValue({
      id: 'u1',
      username: 'seeduser',
      bio: 'Seeded dev user.',
      avatarUrl: null,
      createdAt: new Date().toISOString(),
      // The follow-related fields are part of the UserProfile shape since #85.
      // Including them here keeps the fixture in sync with the API contract even
      // though this test doesn't assert against them.
      followerCount: 0,
      followingCount: 0,
      followedByMe: null,
      clips: [
        {
          id: 'clp_01',
          title: 'Seed Clip 01',
          description: null,
          thumbnailUrl: 'https://cdn.test/thumb.jpg',
          durationSecs: 5,
          viewCount: 0,
          likeCount: 0,
          createdAt: new Date().toISOString(),
          author: { id: 'u1', username: 'seeduser', avatarUrl: null },
          game: null,
          tags: [],
          likedByMe: false,
          shareCode: 'seed01',
        },
      ],
    })
    const router = makeRouter()
    await router.push({ name: 'user', params: { username: 'seeduser' } })
    await router.isReady()
    const wrapper = mount(UserView, { global: { plugins: [router, createPinia()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Seed Clip 01')
    // Bug #1 guard: the clip thumbnail renders, bound to thumbnailUrl.
    expect(wrapper.find('.feed-grid img').attributes('src')).toBe('https://cdn.test/thumb.jpg')

    // Onward-navigation guard (issue #92): clicking a clip card routes to its detail page.
    await wrapper.find('.feed-grid article').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('clip')
    expect(router.currentRoute.value.params.id).toBe('clp_01')
  })
})
