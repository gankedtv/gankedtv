import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { createRouter, createMemoryHistory, type Router } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent, h } from 'vue'
import { useAuthStore } from '@/stores/auth'
import type { ClipDetail, ClipFeedItem } from '@/api/clips'
import ReelClip from '../ReelClip.vue'

const like = vi.fn()
const unlike = vi.fn()
const recordView = vi.fn()
vi.mock('@/api/clips', async () => {
  const actual = await vi.importActual<typeof import('@/api/clips')>('@/api/clips')
  return {
    ...actual,
    clips: {
      ...actual.clips,
      like: (id: string) => like(id),
      unlike: (id: string) => unlike(id),
      recordView: (id: string) => recordView(id),
    },
  }
})

function makeRouter(): Router {
  const stub = defineComponent({ render: () => h('div') })
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: stub },
      { path: '/login', name: 'login', component: stub },
      { path: '/clip/:id', name: 'clip', component: stub },
      { path: '/user/:username', name: 'user', component: stub },
      { path: '/feed/reels', name: 'reels', component: stub },
      { path: '/feed/reels/:id', name: 'reel-clip', component: stub },
    ],
  })
}

function makeClip(overrides: Partial<ClipFeedItem> = {}): ClipFeedItem {
  return {
    id: 'clp_01',
    title: 'No-scope wallbang',
    description: null,
    thumbnailUrl: 'https://cdn.test/thumb.jpg',
    durationSecs: 12,
    viewCount: 42,
    likeCount: 7,
    createdAt: new Date().toISOString(),
    author: { id: 'u1', username: 'reelsuser', avatarUrl: null },
    game: { id: 1, name: 'Valorant', slug: 'valorant', tag: 'VALORANT' },
    tags: [],
    likedByMe: false,
    shareCode: 'sc01',
    ...overrides,
  }
}

function makeDetail(overrides: Partial<ClipDetail> = {}): ClipDetail {
  return {
    ...makeClip(),
    videoUrl: 'https://cdn.test/video.mp4',
    videoUrlExpiresAt: new Date(Date.now() + 60_000).toISOString(),
    videoCodec: null,
    width: 1920,
    height: 1080,
    visibility: 'public',
    ...overrides,
  }
}

// Track mounted wrappers so afterEach can unmount them — this is what
// actually removes teleported nodes cleanly. Just nuking document.body races
// with Vue's pending microtasks and crashes the next render.
const mountedWrappers: VueWrapper[] = []

async function mountReel(props: {
  clip?: ClipFeedItem
  detail?: ClipDetail | null
  detailErrored?: boolean
  isActive?: boolean
  globalMuted?: boolean
}) {
  const router = makeRouter()
  await router.push('/feed/reels/clp_01')
  await router.isReady()
  // One pinia for both the test (setActivePinia, so useAuthStore() in the test
  // body talks to the same store) and the mount (plugins). Without sharing
  // the instance, auth.setUser in the test body mutates a different pinia
  // than the component reads from.
  const pinia = createPinia()
  setActivePinia(pinia)
  const wrapper = mount(ReelClip, {
    props: {
      clip: props.clip ?? makeClip(),
      detail: props.detail ?? null,
      detailErrored: props.detailErrored ?? false,
      isActive: props.isActive ?? false,
      globalMuted: props.globalMuted ?? true,
    },
    global: {
      plugins: [router, pinia],
    },
  })
  mountedWrappers.push(wrapper)
  return wrapper
}

beforeEach(() => {
  like.mockReset()
  unlike.mockReset()
  recordView.mockReset()
  // jsdom's HTMLMediaElement.play/pause are unimplemented stubs that log
  // "Not implemented" to stderr. The component defensively wraps the return
  // in Promise.resolve, so the code path works — silencing the noise here.
  HTMLMediaElement.prototype.play = vi.fn(() => Promise.resolve())
  HTMLMediaElement.prototype.pause = vi.fn()
})

afterEach(() => {
  // The comments bottom sheet is <Teleport to="body">, so its DOM persists
  // across tests if we don't clean it up. Without this, "is the sheet open?"
  // assertions in later tests false-positive on stale nodes from earlier
  // tests in the file.
  while (mountedWrappers.length) mountedWrappers.pop()!.unmount()
})

describe('ReelClip — render gates', () => {
  it('shows the thumbnail and no <video> when detail is null', async () => {
    const wrapper = await mountReel({ detail: null })
    expect(wrapper.find('img').attributes('src')).toBe('https://cdn.test/thumb.jpg')
    expect(wrapper.find('video').exists()).toBe(false)
  })

  it('mounts the <video> with the detail.videoUrl once detail arrives', async () => {
    const wrapper = await mountReel({ detail: makeDetail() })
    const video = wrapper.find('video')
    expect(video.exists()).toBe(true)
    expect(video.attributes('src')).toBe('https://cdn.test/video.mp4')
    expect(video.attributes('poster')).toBe('https://cdn.test/thumb.jpg')
  })

  it('shows a retry overlay when detail loading failed', async () => {
    const wrapper = await mountReel({ detail: null, detailErrored: true })
    expect(wrapper.text()).toContain("Couldn't load video")
    const retry = wrapper.findAll('button').find((b) => b.text() === 'Retry')
    expect(retry).toBeDefined()
    await retry!.trigger('click')
    expect(wrapper.emitted('retry-detail')).toBeTruthy()
    expect(wrapper.emitted('retry-detail')![0]).toEqual(['clp_01'])
  })
})

describe('ReelClip — like flow', () => {
  it('optimistically flips like state and emits liked-changed on success', async () => {
    like.mockResolvedValue({ liked: true, likeCount: 8 })
    const wrapper = await mountReel({ detail: makeDetail() })
    const auth = useAuthStore()
    auth.setUser({
      id: 'u1',
      username: 'me',
      email: null,
      bio: null,
      avatarUrl: null,
      createdAt: '',
      hasPassword: true,
    })

    const likeBtn = wrapper.find('button[aria-label="Like"]')
    expect(likeBtn.exists()).toBe(true)
    await likeBtn.trigger('click')
    // Optimistic flip BEFORE the await resolves.
    expect(wrapper.find('button[aria-label="Unlike"]').exists()).toBe(true)
    await flushPromises()
    expect(like).toHaveBeenCalledWith('clp_01')
    const events = wrapper.emitted('liked-changed')!
    expect(events[events.length - 1]).toEqual([{ id: 'clp_01', liked: true, count: 8 }])
  })

  it('rolls back the optimistic flip when the API call fails', async () => {
    like.mockRejectedValue(new Error('boom'))
    const wrapper = await mountReel({ detail: makeDetail() })
    const auth = useAuthStore()
    auth.setUser({
      id: 'u1',
      username: 'me',
      email: null,
      bio: null,
      avatarUrl: null,
      createdAt: '',
      hasPassword: true,
    })

    await wrapper.find('button[aria-label="Like"]').trigger('click')
    await flushPromises()
    // Rolled back to the original (un-liked) state, no liked-changed emit.
    // The optimistic flip happens synchronously inside the handler, but
    // microtasks (including the rejected promise's catch) all flush before
    // the await trigger returns — so we only assert the final state.
    expect(wrapper.find('button[aria-label="Like"]').exists()).toBe(true)
    expect(wrapper.emitted('liked-changed')).toBeFalsy()
  })

  it('redirects anonymous users to /login with the reels-route as redirect query', async () => {
    const wrapper = await mountReel({ detail: makeDetail() })
    // No auth.setUser — auth.isAuthenticated is false.
    await wrapper.find('button[aria-label="Like"]').trigger('click')
    await flushPromises()
    expect(like).not.toHaveBeenCalled()
    // Router push lands on /login with redirect=/feed/reels/clp_01.
    const router = wrapper.vm.$router
    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/feed/reels/clp_01')
  })
})

describe('ReelClip — mute toggle', () => {
  it('emits toggle-mute when the mute button is clicked', async () => {
    const wrapper = await mountReel({ detail: makeDetail(), globalMuted: true })
    await wrapper.find('button[aria-label="Unmute"]').trigger('click')
    expect(wrapper.emitted('toggle-mute')).toBeTruthy()
  })

  it('renders the unmute icon while muted and the mute icon while unmuted', async () => {
    const muted = await mountReel({ detail: makeDetail(), globalMuted: true })
    expect(muted.find('button[aria-label="Unmute"]').exists()).toBe(true)
    expect(muted.find('button[aria-label="Mute"]').exists()).toBe(false)

    const unmuted = await mountReel({ detail: makeDetail(), globalMuted: false })
    expect(unmuted.find('button[aria-label="Mute"]').exists()).toBe(true)
    expect(unmuted.find('button[aria-label="Unmute"]').exists()).toBe(false)
  })
})

describe('ReelClip — links', () => {
  it('links the author handle to /user/:username', async () => {
    const wrapper = await mountReel({ detail: makeDetail() })
    const userLink = wrapper.findAll('a').find((a) => a.attributes('href') === '/user/reelsuser')
    expect(userLink).toBeDefined()
  })

  it('does not render the open-in-detail link until the comments sheet is opened', async () => {
    const wrapper = await mountReel({ detail: makeDetail() })
    // The 'View full clip →' link lives inside the bottom-sheet header,
    // teleported to body. It only exists once the sheet is mounted.
    expect(
      wrapper.findAll('a').find((a) => a.attributes('href') === '/clip/clp_01'),
    ).toBeUndefined()
  })
})

describe('ReelClip — comments sheet', () => {
  it('opens the comments bottom sheet when the comments button is clicked', async () => {
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    expect(document.querySelector('[role="dialog"][aria-label="Comments"]')).toBeNull()
    await wrapper.find('button[aria-label="Open comments"]').trigger('click')
    await flushPromises()
    expect(document.querySelector('[role="dialog"][aria-label="Comments"]')).not.toBeNull()
  })

  it('surfaces the View-full-clip link inside the open sheet', async () => {
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await wrapper.find('button[aria-label="Open comments"]').trigger('click')
    await flushPromises()
    const link = document.querySelector('a[href="/clip/clp_01"]')
    expect(link).not.toBeNull()
    expect(link?.textContent).toContain('View full clip')
  })

  it('closes the sheet when the active prop flips false (e.g. user scrolls to the next reel)', async () => {
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await wrapper.find('button[aria-label="Open comments"]').trigger('click')
    await flushPromises()
    expect(document.querySelector('[role="dialog"][aria-label="Comments"]')).not.toBeNull()
    await wrapper.setProps({ isActive: false })
    await flushPromises()
    // Vue's <Transition> waits for transitionend before unmounting; jsdom
    // never fires that event, so we have to manually trigger it on the
    // leaving element. Fire on every direct child of body just before the
    // assertion — covers both the backdrop and the sheet wrappers.
    document
      .querySelectorAll('[role="dialog"][aria-label="Comments"], .fixed.inset-0')
      .forEach((el) => el.dispatchEvent(new Event('transitionend')))
    await flushPromises()
    expect(document.querySelector('[role="dialog"][aria-label="Comments"]')).toBeNull()
  })

  it('closes the sheet when the close button is clicked', async () => {
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await wrapper.find('button[aria-label="Open comments"]').trigger('click')
    await flushPromises()
    const closeBtn = document.querySelector(
      'button[aria-label="Close comments"]',
    ) as HTMLElement | null
    expect(closeBtn).not.toBeNull()
    closeBtn?.click()
    await flushPromises()
    // Vue's <Transition> waits for transitionend before unmounting; jsdom
    // never fires that event, so we have to manually trigger it on the
    // leaving element. Fire on every direct child of body just before the
    // assertion — covers both the backdrop and the sheet wrappers.
    document
      .querySelectorAll('[role="dialog"][aria-label="Comments"], .fixed.inset-0')
      .forEach((el) => el.dispatchEvent(new Event('transitionend')))
    await flushPromises()
    expect(document.querySelector('[role="dialog"][aria-label="Comments"]')).toBeNull()
  })
})
