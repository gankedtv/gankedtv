import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory, type Router } from 'vue-router'
import { createPinia } from 'pinia'
import { defineComponent, h } from 'vue'
import type { ClipDetail, ClipFeedItem, ClipFeedPage } from '@/api/clips'
import { ApiError } from '@/api/client'

const feed = vi.fn()
const getDetail = vi.fn()
const recordView = vi.fn()
vi.mock('@/api/clips', async () => {
  const actual = await vi.importActual<typeof import('@/api/clips')>('@/api/clips')
  return {
    ...actual,
    clips: {
      ...actual.clips,
      feed: (q?: unknown) => feed(q),
      getDetail: (id: string) => getDetail(id),
      recordView: (id: string) => recordView(id),
    },
  }
})

import ReelsView from '../ReelsView.vue'

// jsdom doesn't ship IntersectionObserver. The implementation never relies on
// it firing during these tests (we drive activeIndex through direct route
// navigation instead), but the constructor must exist so onMounted doesn't
// throw before pagination/URL-sync logic runs.
class FakeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
beforeEach(() => {
  feed.mockReset()
  getDetail.mockReset()
  recordView.mockReset()
  // Default-resolve getDetail so the prefetch watcher (which fires for the
  // active clip and its neighbors whenever items[] is non-empty) never hits
  // an undefined return. Tests that care about prefetch calls re-stub it.
  getDetail.mockImplementation((id: string) => Promise.resolve(makeDetail(id)))
  vi.stubGlobal('IntersectionObserver', FakeObserver)
  // Silence jsdom's unimplemented-media-element warnings; same rationale as
  // in the ReelClip spec.
  HTMLMediaElement.prototype.play = vi.fn(() => Promise.resolve())
  HTMLMediaElement.prototype.pause = vi.fn()
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
      { path: '/upload', name: 'upload', component: stub },
      { path: '/feed/reels', name: 'reels', component: ReelsView },
      { path: '/feed/reels/:id', name: 'reel-clip', component: ReelsView },
    ],
  })
}

function makeClip(id: string, overrides: Partial<ClipFeedItem> = {}): ClipFeedItem {
  return {
    id,
    title: `Clip ${id}`,
    description: null,
    thumbnailUrl: `https://cdn.test/${id}.jpg`,
    durationSecs: 10,
    viewCount: 0,
    likeCount: 0,
    createdAt: new Date().toISOString(),
    author: { id: 'u1', username: 'creator', avatarUrl: null },
    game: null,
    tags: [],
    likedByMe: false,
    shareCode: id,
    ...overrides,
  }
}

function makeDetail(id: string, overrides: Partial<ClipDetail> = {}): ClipDetail {
  return {
    ...makeClip(id),
    videoUrl: `https://cdn.test/${id}.mp4`,
    videoUrlExpiresAt: new Date(Date.now() + 60_000).toISOString(),
    videoCodec: null,
    width: 1920,
    height: 1080,
    visibility: 'public',
    importSourceUrl: null,
    uploadSource: 'web',
    editedAt: null,
    ...overrides,
  }
}

function makePage(items: ClipFeedItem[], nextCursor: string | null = null): ClipFeedPage {
  return { items, nextCursor }
}

async function mountAt(path: string) {
  const router = makeRouter()
  await router.push(path)
  await router.isReady()
  const wrapper = mount(ReelsView, { global: { plugins: [router, createPinia()] } })
  return { wrapper, router }
}

describe('ReelsView — single-root invariant', () => {
  // The route-level <Transition mode="out-in"> in App.vue can only animate a
  // single-root component. A leading comment node before the root element
  // makes the component multi-root — Transition's leave never resolves and
  // the NEXT route's view never mounts, so navigating from /login to
  // /feed/reels/:id (post-like-redirect) renders a blank page until refresh.
  // Same regression UserView.spec.ts guards against (issue #92).
  it('renders a single element root, not a leading-comment fragment', async () => {
    feed.mockReturnValue(new Promise(() => {}))
    const { wrapper } = await mountAt('/feed/reels')
    expect(wrapper.html().trimStart().startsWith('<!--')).toBe(false)
    expect(wrapper.html().trimStart().startsWith('<div')).toBe(true)
  })
})

describe('ReelsView — initial load', () => {
  it('loads the public feed when no seed id is provided', async () => {
    feed.mockResolvedValue(makePage([makeClip('a'), makeClip('b')], 'cursor-1'))
    const { wrapper } = await mountAt('/feed/reels')
    await flushPromises()
    expect(feed).toHaveBeenCalledWith({ limit: 20 })
    expect(wrapper.text()).toContain('Clip a')
    expect(wrapper.text()).toContain('Clip b')
  })

  it('shows an error state when initial load fails', async () => {
    feed.mockRejectedValue(new Error('network down'))
    const { wrapper } = await mountAt('/feed/reels')
    await flushPromises()
    expect(wrapper.text()).toContain("Couldn't load reels")
  })

  it('shows the empty state when the feed returns zero clips', async () => {
    feed.mockResolvedValue(makePage([]))
    const { wrapper } = await mountAt('/feed/reels')
    await flushPromises()
    expect(wrapper.text()).toContain('No clips yet')
    expect(wrapper.find('a[href="/upload"]').exists()).toBe(true)
  })
})

describe('ReelsView — deep link', () => {
  it('dedupes the seed clip when it also appears in the first page', async () => {
    const seed = makeDetail('seed')
    getDetail.mockResolvedValue(seed)
    feed.mockResolvedValue(makePage([makeClip('seed'), makeClip('b'), makeClip('c')]))
    const { wrapper } = await mountAt('/feed/reels/seed')
    await flushPromises()

    // The seed appears exactly once (head), followed by the rest of the page.
    const html = wrapper.html()
    const firstIdx = html.indexOf('Clip seed')
    const lastIdx = html.lastIndexOf('Clip seed')
    expect(firstIdx).toBeGreaterThanOrEqual(0)
    expect(firstIdx).toBe(lastIdx)
    expect(wrapper.text()).toContain('Clip b')
    expect(wrapper.text()).toContain('Clip c')
  })

  it('falls back to the top of the feed and rewrites the URL when the seed 404s', async () => {
    getDetail.mockImplementation((id: string) => {
      if (id === 'missing') return Promise.reject(new ApiError(404, null))
      return Promise.resolve(makeDetail(id))
    })
    feed.mockResolvedValue(makePage([makeClip('a'), makeClip('b')]))
    const { wrapper, router } = await mountAt('/feed/reels/missing')
    await flushPromises()

    // Items render from the feed page; missing seed not prepended.
    expect(wrapper.text()).toContain('Clip a')
    expect(wrapper.text()).not.toContain('Clip missing')
    // URL stripped back to the bare /feed/reels route. (URL-sync may have
    // since pushed it forward to /feed/reels/a once activeIndex settles, so
    // accept either the bare route or the first-item URL.)
    const name = router.currentRoute.value.name
    expect(['reels', 'reel-clip']).toContain(name)
    if (name === 'reel-clip') {
      expect(router.currentRoute.value.params.id).toBe('a')
    }
  })

  it('surfaces the initial error when the feed itself fails even if the seed succeeds', async () => {
    getDetail.mockResolvedValue(makeDetail('seed'))
    feed.mockRejectedValue(new Error('feed down'))
    const { wrapper } = await mountAt('/feed/reels/seed')
    await flushPromises()
    expect(wrapper.text()).toContain("Couldn't load reels")
  })
})

describe('ReelsView — URL sync', () => {
  it('replaces the URL with the seed clip id once the deep-link load resolves', async () => {
    getDetail.mockResolvedValue(makeDetail('seed'))
    feed.mockResolvedValue(makePage([makeClip('b')]))
    const { router } = await mountAt('/feed/reels/seed')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('reel-clip')
    expect(router.currentRoute.value.params.id).toBe('seed')
  })

  it('replaces the URL with the first clip id on a bare /feed/reels mount', async () => {
    feed.mockResolvedValue(makePage([makeClip('first'), makeClip('second')]))
    const { router } = await mountAt('/feed/reels')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('reel-clip')
    expect(router.currentRoute.value.params.id).toBe('first')
  })
})

describe('ReelsView — detail prefetch', () => {
  it('fetches details for the active clip and its immediate neighbor on mount', async () => {
    feed.mockResolvedValue(makePage([makeClip('a'), makeClip('b'), makeClip('c')]))
    getDetail.mockImplementation((id: string) => Promise.resolve(makeDetail(id)))
    await mountAt('/feed/reels')
    await flushPromises()
    // Index 0 is active → window = [undefined, 'a', 'b'] → fetch a + b.
    expect(getDetail).toHaveBeenCalledWith('a')
    expect(getDetail).toHaveBeenCalledWith('b')
    expect(getDetail).not.toHaveBeenCalledWith('c')
  })
})
