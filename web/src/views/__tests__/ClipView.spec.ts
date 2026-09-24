import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { createRouter, createMemoryHistory, type Router } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import { defineComponent, h } from 'vue'
import type { ClipDetail } from '@/api/clips'

// Neither survives jsdom. The Plyr double records construction so specs can assert autoplay
// runs after the player exists — the ordering the real Plyr requires.
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
type HlsHandler = (event: string, data: { fatal: boolean; type: string; details: string }) => void
const hlsMock = vi.hoisted(() => ({
  supported: false,
  instances: [] as { handlers: Record<string, HlsHandler>; recoverMediaError: () => void }[],
}))
vi.mock('hls.js', () => ({
  default: class {
    static isSupported() {
      return hlsMock.supported
    }
    static Events = { MANIFEST_PARSED: 'hlsManifestParsed', ERROR: 'hlsError' }
    static ErrorTypes = { MEDIA_ERROR: 'mediaError', NETWORK_ERROR: 'networkError' }
    levels = []
    handlers: Record<string, HlsHandler> = {}
    recoverMediaError = vi.fn()
    constructor() {
      hlsMock.instances.push(this)
    }
    loadSource() {}
    attachMedia() {}
    on(event: string, handler: HlsHandler) {
      this.handlers[event] = handler
    }
    destroy() {}
  },
}))
vi.mock('@/lib/sentry', () => ({ reportPlaybackFailure: vi.fn(), notePlaybackRecovery: vi.fn() }))

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
import { reportPlaybackFailure } from '@/lib/sentry'

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

async function mountClip({ signedIn = false, attach = false } = {}): Promise<VueWrapper> {
  const router = makeRouter()
  await router.push({ name: 'clip', params: { id: 'clp_01' } })
  await router.isReady()
  const pinia = createPinia()
  setActivePinia(pinia)
  if (signedIn) {
    // A signed-in non-owner is the only viewer the Report button renders for.
    useAuthStore().user = { id: 'u2', username: 'viewer' } as never
  }
  const wrapper = mount(ClipView, {
    global: { plugins: [router, pinia] },
    // Only an attached tree bubbles up to window.
    ...(attach ? { attachTo: document.body } : {}),
  })
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
  hlsMock.supported = false
  hlsMock.instances.length = 0
  vi.mocked(reportPlaybackFailure).mockReset()
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
  // Not inline, so a failing assertion can't leak a spy into later tests.
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
    // The failed attempt restores the viewer's own mute state (false here), rather than
    // forcing sound on for the click that follows.
    expect(wrapper.find('video').element.muted).toBe(false)
  })

  it('retires the tap overlay when playback starts from Plyr\u2019s own controls', async () => {
    // The overlay clears the control bar, so playback can start without it being clicked.
    play.mockRejectedValue(new DOMException('blocked', 'NotAllowedError'))
    const wrapper = await mountClip()
    expect(wrapper.find('button[aria-label="Play No-scope wallbang"]').exists()).toBe(true)

    await wrapper.find('video').trigger('play')

    expect(wrapper.find('button[aria-label="Play No-scope wallbang"]').exists()).toBe(false)
  })

  it('shows the unmute badge when the first attempt succeeds already-muted', async () => {
    // Plyr restores mute state, so a viewer muted last visit plays muted on the first attempt.
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

describe('ClipView playback errors', () => {
  const PLAYBACK_ERROR = "This clip couldn't be played. Try again in a moment."

  function video(wrapper: VueWrapper): HTMLVideoElement {
    return wrapper.find('video').element as HTMLVideoElement
  }

  async function failMedia(wrapper: VueWrapper) {
    video(wrapper).dispatchEvent(new Event('error'))
    await flushPromises()
    await flushPromises()
  }

  function canDecodeAv1() {
    vi.spyOn(HTMLMediaElement.prototype, 'canPlayType').mockImplementation((type: string) =>
      type.includes('av01') ? 'probably' : '',
    )
  }

  it('falls back to the JIT stream when an AV1 master the browser claimed it could play fails', async () => {
    canDecodeAv1()
    getDetail.mockResolvedValue(makeDetail({ videoCodec: 'av1' }))
    getStream.mockResolvedValue({ status: 'ready', hlsUrl: 'https://cache.test/master.m3u8' })
    const wrapper = await mountClip()
    expect(getStream).not.toHaveBeenCalled()

    await failMedia(wrapper)

    expect(getStream).toHaveBeenCalledWith('clp_01')
    expect(reportPlaybackFailure).not.toHaveBeenCalled()
  })

  it('retries an h264 master once with a freshly signed URL', async () => {
    getDetail
      .mockResolvedValueOnce(makeDetail({ videoUrl: 'https://cdn.test/expired.mp4' }))
      .mockResolvedValueOnce(makeDetail({ videoUrl: 'https://cdn.test/fresh.mp4' }))
    const wrapper = await mountClip()
    expect(video(wrapper).getAttribute('src')).toBe('https://cdn.test/expired.mp4')

    await failMedia(wrapper)

    expect(getDetail).toHaveBeenCalledTimes(2)
    expect(video(wrapper).getAttribute('src')).toBe('https://cdn.test/fresh.mp4')
    expect(wrapper.text()).not.toContain(PLAYBACK_ERROR)
  })

  it('shows the error panel and reports once the retry fails too', async () => {
    const wrapper = await mountClip()

    await failMedia(wrapper)
    await failMedia(wrapper)

    expect(wrapper.text()).toContain(PLAYBACK_ERROR)
    expect(wrapper.find('video').exists()).toBe(false)
    expect(reportPlaybackFailure).toHaveBeenCalledTimes(1)
    expect(reportPlaybackFailure).toHaveBeenCalledWith(
      expect.objectContaining({ clipId: 'clp_01', mode: 'direct', codec: 'h264' }),
    )
  })

  it('fails straight to the error panel when the refetch for a fresh URL fails', async () => {
    getDetail.mockResolvedValueOnce(makeDetail()).mockRejectedValueOnce(new Error('offline'))
    const wrapper = await mountClip()

    await failMedia(wrapper)

    expect(wrapper.text()).toContain(PLAYBACK_ERROR)
    expect(reportPlaybackFailure).toHaveBeenCalledTimes(1)
  })

  it('starts over with a fresh recovery budget after Retry', async () => {
    const wrapper = await mountClip()
    await failMedia(wrapper)
    await failMedia(wrapper)

    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Retry')!
      .trigger('click')
    await flushPromises()
    await flushPromises()
    await failMedia(wrapper)

    expect(wrapper.text()).not.toContain(PLAYBACK_ERROR)
    expect(wrapper.find('video').exists()).toBe(true)
  })

  it('gives a clip that played since its last recovery another retry', async () => {
    const wrapper = await mountClip()

    await failMedia(wrapper)
    video(wrapper).dispatchEvent(new Event('playing'))
    await failMedia(wrapper)

    expect(getDetail).toHaveBeenCalledTimes(3)
    expect(wrapper.text()).not.toContain(PLAYBACK_ERROR)
  })

  it('caps recoveries per load so a clip that keeps breaking stops restarting', async () => {
    const wrapper = await mountClip()

    for (let i = 0; i < 3; i++) {
      await failMedia(wrapper)
      video(wrapper).dispatchEvent(new Event('playing'))
    }
    await failMedia(wrapper)

    expect(wrapper.text()).toContain(PLAYBACK_ERROR)
    expect(reportPlaybackFailure).toHaveBeenCalledTimes(1)
  })

  it('sends a master with no recorded codec to the JIT stream', async () => {
    getDetail.mockResolvedValue(makeDetail({ videoCodec: null }))
    getStream.mockResolvedValue({ status: 'ready', hlsUrl: 'https://cache.test/master.m3u8' })
    const wrapper = await mountClip()

    await failMedia(wrapper)

    expect(getStream).toHaveBeenCalledWith('clp_01')
    expect(getDetail).toHaveBeenCalledTimes(1)
  })

  it('resumes where playback failed instead of replaying a paused clip from the start', async () => {
    const wrapper = await mountClip()
    expect(play).toHaveBeenCalledTimes(1)
    Object.defineProperty(video(wrapper), 'currentTime', { value: 40, configurable: true })

    await failMedia(wrapper)
    const restarted = video(wrapper)
    let resumedAt = 0
    Object.defineProperty(restarted, 'currentTime', {
      configurable: true,
      get: () => resumedAt,
      set: (t: number) => (resumedAt = t),
    })
    restarted.dispatchEvent(new Event('loadedmetadata'))

    expect(resumedAt).toBe(40)
    expect(play).toHaveBeenCalledTimes(1)
  })

  it("keeps the player's bubbling error event away from window.onerror", async () => {
    const wrapper = await mountClip({ attach: true })
    const onWindowError = vi.fn()
    window.addEventListener('error', onWindowError)

    video(wrapper).dispatchEvent(new CustomEvent('error', { bubbles: true }))
    window.removeEventListener('error', onWindowError)

    expect(onWindowError).not.toHaveBeenCalled()
  })

  describe('with hls.js', () => {
    beforeEach(() => {
      hlsMock.supported = true
      getDetail.mockResolvedValue(makeDetail({ videoCodec: 'av1' }))
      getStream.mockResolvedValue({ status: 'ready', hlsUrl: 'https://cache.test/master.m3u8' })
    })

    const fatal = (type: string, details: string) => ({ fatal: true, type, details })

    it('tries hls.js media recovery before anything else', async () => {
      const wrapper = await mountClip()
      const hls = hlsMock.instances[0]!

      hls.handlers.hlsError!('hlsError', fatal('mediaError', 'bufferAppendError'))
      await flushPromises()

      expect(hls.recoverMediaError).toHaveBeenCalledTimes(1)
      expect(getStream).toHaveBeenCalledTimes(1)
      expect(wrapper.text()).not.toContain(PLAYBACK_ERROR)
    })

    it('ignores non-fatal errors', async () => {
      await mountClip()
      const hls = hlsMock.instances[0]!

      hls.handlers.hlsError!('hlsError', {
        fatal: false,
        type: 'mediaError',
        details: 'bufferStalledError',
      })

      expect(hls.recoverMediaError).not.toHaveBeenCalled()
    })

    it('re-requests the stream once, then gives up with a report', async () => {
      const wrapper = await mountClip()

      hlsMock.instances[0]!.handlers.hlsError!('hlsError', fatal('networkError', 'fragLoadError'))
      await flushPromises()
      await flushPromises()
      expect(getStream).toHaveBeenCalledTimes(2)

      hlsMock.instances[1]!.handlers.hlsError!('hlsError', fatal('networkError', 'fragLoadError'))
      await flushPromises()

      expect(wrapper.text()).toContain(PLAYBACK_ERROR)
      expect(reportPlaybackFailure).toHaveBeenCalledWith(
        expect.objectContaining({ mode: 'hls.js', codec: 'av1', detail: 'fragLoadError' }),
      )
    })
  })
})
