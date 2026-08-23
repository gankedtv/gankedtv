import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { createRouter, createMemoryHistory, type Router } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import { defineComponent, h } from 'vue'
import type { ClipDetail } from '@/api/clips'

// Plyr and hls.js are imported at module scope by ClipView and neither survives jsdom, so both
// are replaced wholesale. The Plyr double records construction so the specs can assert that
// autoplay runs after the player exists, which is the ordering the real Plyr requires.
const plyrInstances: { destroy: () => void }[] = []
vi.mock('plyr', () => ({
  default: class {
    constructor() {
      plyrInstances.push(this as unknown as { destroy: () => void })
    }
    destroy() {}
  },
}))
vi.mock('plyr/dist/plyr.css', () => ({}))
vi.mock('hls.js', () => ({
  default: class {
    static isSupported() {
      return false
    }
    static Events = { MANIFEST_PARSED: 'hlsManifestParsed' }
    levels = []
    loadSource() {}
    attachMedia() {}
    on() {}
    destroy() {}
  },
}))

const getDetail = vi.fn()
const getByShareCode = vi.fn()
const recordView = vi.fn()
const getStream = vi.fn()
vi.mock('@/api/clips', async () => {
  const actual = await vi.importActual<typeof import('@/api/clips')>('@/api/clips')
  return {
    ...actual,
    clips: {
      ...actual.clips,
      getDetail: (id: string) => getDetail(id),
      getByShareCode: (code: string) => getByShareCode(code),
      recordView: (id: string) => recordView(id),
      getStream: (id: string) => getStream(id),
    },
  }
})
vi.mock('@/api/games', () => ({ games: { clips: vi.fn().mockResolvedValue({ items: [] }) } }))
vi.mock('@/api/comments', () => ({
  comments: { list: vi.fn().mockResolvedValue({ items: [], nextCursor: null }) },
}))

import ClipView from '../ClipView.vue'

function makeRouter(): Router {
  const stub = defineComponent({ render: () => h('div') })
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: stub },
      { path: '/login', name: 'login', component: stub },
      { path: '/clip/:id', name: 'clip', component: ClipView },
      { path: '/c/:code', name: 'clip-share', component: stub },
      { path: '/user/:username', name: 'user', component: stub },
      { path: '/game/:slug', name: 'game-detail', component: stub },
      { path: '/tag/:slug', name: 'tag-detail', component: stub },
      { path: '/:pathMatch(.*)*', name: 'not-found', component: stub },
    ],
  })
}

function makeDetail(overrides: Partial<ClipDetail> = {}): ClipDetail {
  return {
    id: 'clp_01',
    shareCode: 'sc01',
    title: 'No-scope wallbang',
    description: null,
    videoUrl: 'https://cdn.test/clip.mp4',
    videoUrlExpiresAt: new Date(Date.now() + 3_600_000).toISOString(),
    videoCodec: 'h264',
    thumbnailUrl: 'https://cdn.test/thumb.jpg',
    durationSecs: 12,
    width: 1920,
    height: 1080,
    viewCount: 42,
    likeCount: 7,
    createdAt: new Date().toISOString(),
    author: { id: 'u1', username: 'clipuser', avatarUrl: null },
    game: null,
    tags: [],
    likedByMe: false,
    visibility: 'public',
    importSourceUrl: null,
    uploadSource: 'web',
    editedAt: null,
    ...overrides,
  } as ClipDetail
}

const wrappers: VueWrapper[] = []

async function mountClip({ signedIn = false } = {}): Promise<VueWrapper> {
  const router = makeRouter()
  await router.push({ name: 'clip', params: { id: 'clp_01' } })
  await router.isReady()
  const pinia = createPinia()
  setActivePinia(pinia)
  if (signedIn) {
    // A signed-in non-owner is the only viewer the Report button renders for.
    useAuthStore().user = { id: 'u2', username: 'viewer' } as never
  }
  const wrapper = mount(ClipView, { global: { plugins: [router, pinia] } })
  wrappers.push(wrapper)
  await flushPromises()
  await flushPromises()
  return wrapper
}

function setReducedMotion(reduce: boolean) {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches: reduce && query.includes('prefers-reduced-motion'),
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  })) as unknown as typeof window.matchMedia
}

let play: ReturnType<typeof vi.fn>

beforeEach(() => {
  plyrInstances.length = 0
  getDetail.mockReset().mockResolvedValue(makeDetail())
  recordView.mockReset()
  getStream.mockReset()
  play = vi.fn(() => Promise.resolve())
  HTMLMediaElement.prototype.play = play as unknown as HTMLMediaElement['play']
  HTMLMediaElement.prototype.pause = vi.fn()
  setReducedMotion(false)
})

afterEach(() => {
  while (wrappers.length) wrappers.pop()!.unmount()
  // Spies are restored here, not inline, so a failing assertion can't leak one into the
  // tests that follow.
  vi.restoreAllMocks()
})

describe('ClipView autoplay', () => {
  it('starts playback on its own once the clip loads', async () => {
    await mountClip()

    expect(play).toHaveBeenCalled()
    expect(plyrInstances).toHaveLength(1)
  })

  it('binds the thumbnail as a poster so a blocked autoplay is not a black box', async () => {
    const wrapper = await mountClip()

    expect(wrapper.find('video').attributes('poster')).toBe('https://cdn.test/thumb.jpg')
  })

  it('retries muted when the browser refuses audible playback', async () => {
    play.mockRejectedValueOnce(new DOMException('blocked', 'NotAllowedError'))
    const wrapper = await mountClip()

    expect(play).toHaveBeenCalledTimes(2)
    expect(wrapper.find('video').element.muted).toBe(true)
    expect(wrapper.text()).toContain('Unmute')
  })

  it('unmutes from the badge and hides it', async () => {
    play.mockRejectedValueOnce(new DOMException('blocked', 'NotAllowedError'))
    const wrapper = await mountClip()

    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Unmute')!
      .trigger('click')

    expect(wrapper.find('video').element.muted).toBe(false)
    expect(wrapper.text()).not.toContain('Unmute')
  })

  it('falls back to a tap-to-play overlay when even muted playback is refused', async () => {
    play.mockRejectedValue(new DOMException('blocked', 'NotAllowedError'))
    const wrapper = await mountClip()

    const tap = wrapper.find('button[aria-label="Play No-scope wallbang"]')
    expect(tap.exists()).toBe(true)
    // The failed muted attempt must not leave the element silently muted for the click that
    // follows — the viewer asked for this one.
    expect(wrapper.find('video').element.muted).toBe(false)
  })

  it('retires the tap overlay when playback starts from Plyr\u2019s own controls', async () => {
    // Plyr's control bar and large play button render above our overlay, so the viewer can start
    // the clip without touching it — leaving a play circle over a playing video.
    play.mockRejectedValue(new DOMException('blocked', 'NotAllowedError'))
    const wrapper = await mountClip()
    expect(wrapper.find('button[aria-label="Play No-scope wallbang"]').exists()).toBe(true)

    await wrapper.find('video').trigger('play')

    expect(wrapper.find('button[aria-label="Play No-scope wallbang"]').exists()).toBe(false)
  })

  it('shows the unmute badge when the first attempt succeeds already-muted', async () => {
    // Plyr restores mute state across visits, so a viewer we had to mute yesterday plays muted
    // today on the first attempt — with nothing to explain the silence unless we say so.
    Object.defineProperty(HTMLMediaElement.prototype, 'muted', {
      configurable: true,
      get: () => true,
      set: () => {},
    })
    try {
      const wrapper = await mountClip()

      expect(play).toHaveBeenCalledTimes(1)
      expect(wrapper.text()).toContain('Unmute')
    } finally {
      delete (HTMLMediaElement.prototype as unknown as Record<string, unknown>).muted
    }
  })

  it('does not autoplay when the viewer asked for reduced motion', async () => {
    setReducedMotion(true)

    await mountClip()

    expect(play).not.toHaveBeenCalled()
  })

  it('does not autoplay into a backgrounded tab', async () => {
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue(
      'hidden' as DocumentVisibilityState,
    )

    await mountClip()

    expect(play).not.toHaveBeenCalled()
  })

  it('pauses the player when a dialog opens over it', async () => {
    const wrapper = await mountClip({ signedIn: true })

    await wrapper
      .findAll('button')
      .find((b) => b.text().includes('Report'))!
      .trigger('click')
    await flushPromises()

    expect(HTMLMediaElement.prototype.pause).toHaveBeenCalled()
  })
})
