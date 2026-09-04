import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { createRouter, createMemoryHistory, type Router } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import { useAuthStore } from '@/stores/auth'
import type { ClipDetail, ClipFeedItem } from '@/api/clips'
import ReelClip from '../ReelClip.vue'

const detectPosterBars = vi.fn<(url: string) => Promise<unknown>>()
// Only the pixel-reading half is stubbed; the framing math stays real so these tests exercise
// the transform the component actually ships.
vi.mock('@/lib/letterbox', async () => {
  const actual = await vi.importActual<typeof import('@/lib/letterbox')>('@/lib/letterbox')
  return { ...actual, detectPosterBars: (url: string) => detectPosterBars(url) }
})

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
    importSourceUrl: null,
    uploadSource: 'web',
    editedAt: null,
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

// jsdom's play/pause are unimplemented stubs that never move `paused`, so anything that branches
// on playback state would test nothing. Back it with a flag and fire the events the component
// binds, which is what makes "tap pauses a playing clip" a real assertion.
let mediaPaused = true

beforeEach(() => {
  like.mockReset()
  unlike.mockReset()
  recordView.mockReset()
  detectPosterBars.mockReset()
  detectPosterBars.mockResolvedValue(null)
  mediaPaused = true
  Object.defineProperty(HTMLMediaElement.prototype, 'paused', {
    configurable: true,
    get: () => mediaPaused,
  })
  HTMLMediaElement.prototype.play = vi.fn(function (this: HTMLMediaElement) {
    mediaPaused = false
    this.dispatchEvent(new Event('play'))
    return Promise.resolve()
  })
  HTMLMediaElement.prototype.pause = vi.fn(function (this: HTMLMediaElement) {
    mediaPaused = true
    this.dispatchEvent(new Event('pause'))
  })
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
      avatarSource: null,
      oauthAvatarUrl: null,
      bannerUrl: null,
      accentColor: null,
      socialLinks: null,
      createdAt: '',
      hasPassword: true,
      role: 'user',
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
      avatarSource: null,
      oauthAvatarUrl: null,
      bannerUrl: null,
      accentColor: null,
      socialLinks: null,
      createdAt: '',
      hasPassword: true,
      role: 'user',
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

// The playback surface is the only control whose label flips between Play and Pause.
function playbackSurface(wrapper: VueWrapper) {
  return wrapper
    .findAll('button')
    .find((b) => /^(Play|Pause) /.test(b.attributes('aria-label') ?? ''))!
}

// jsdom has no PointerEvent, and VTU's trigger() can't set clientX/detail on the MouseEvent it
// falls back to (both are getter-only). Constructing the event directly is the way to express a
// gesture with real coordinates.
function fire(
  wrapper: ReturnType<typeof playbackSurface>,
  type: string,
  init: MouseEventInit = {},
) {
  const ev = new MouseEvent(type, { bubbles: true, cancelable: true, ...init })
  Object.defineProperty(ev, 'pointerId', { value: 1, configurable: true })
  wrapper.element.dispatchEvent(ev)
  return nextTick()
}

function tap(surface: ReturnType<typeof playbackSurface>, x = 10, y = 10) {
  return fire(surface, 'pointerdown', { clientX: x, clientY: y }).then(() =>
    fire(surface, 'pointerup', { clientX: x, clientY: y }),
  )
}

describe('ReelClip \u2014 pause', () => {
  it('pauses a playing clip on tap and resumes it on the next tap', async () => {
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await flushPromises()
    expect(mediaPaused).toBe(false)

    await tap(playbackSurface(wrapper))
    expect(mediaPaused).toBe(true)

    await tap(playbackSurface(wrapper))
    await flushPromises()
    expect(mediaPaused).toBe(false)
  })

  it('advertises the paused state through the control label', async () => {
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await flushPromises()
    expect(playbackSurface(wrapper).attributes('aria-label')).toBe('Pause No-scope wallbang')

    await tap(playbackSurface(wrapper))
    expect(playbackSurface(wrapper).attributes('aria-label')).toBe('Play No-scope wallbang')
  })

  // Keyboard activation fires a click with no pointer sequence behind it. Pointer taps must not
  // also run that path, or every tap would toggle twice and land back where it started.
  it('toggles from the keyboard without double-toggling on a pointer tap', async () => {
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await flushPromises()
    const surface = playbackSurface(wrapper)

    await fire(surface, 'click', { detail: 0 })
    expect(mediaPaused).toBe(true)

    // A real tap: pointerdown/up (which toggles) plus the click the browser synthesises after it.
    await tap(surface)
    await fire(surface, 'click', { detail: 1 })
    await flushPromises()
    expect(mediaPaused).toBe(false)
  })
})

describe('ReelClip \u2014 hold to skim', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  async function mountHeld() {
    vi.useFakeTimers()
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await vi.runOnlyPendingTimersAsync()
    return {
      wrapper,
      surface: playbackSurface(wrapper),
      video: wrapper.find('video').element as HTMLVideoElement,
    }
  }

  it('runs at 2x while held and restores normal speed on release', async () => {
    const { wrapper, surface, video } = await mountHeld()

    await fire(surface, 'pointerdown', { clientX: 10, clientY: 10 })
    await vi.advanceTimersByTimeAsync(300)
    expect(video.playbackRate).toBe(2)
    expect(wrapper.text()).toContain('2x speed')

    await fire(surface, 'pointerup', { clientX: 10, clientY: 10 })
    expect(video.playbackRate).toBe(1)
    expect(wrapper.text()).not.toContain('2x speed')
  })

  // The release that ends a skim is not also a request to pause.
  it('does not pause the clip when the hold ends', async () => {
    const { surface } = await mountHeld()

    await fire(surface, 'pointerdown', { clientX: 10, clientY: 10 })
    await vi.advanceTimersByTimeAsync(300)
    await fire(surface, 'pointerup', { clientX: 10, clientY: 10 })
    expect(mediaPaused).toBe(false)
  })

  // Most presses in a vertical snap feed are the start of a scroll, not a request to skim.
  it('abandons the hold once the pointer moves like a scroll', async () => {
    const { surface, video } = await mountHeld()

    await fire(surface, 'pointerdown', { clientX: 10, clientY: 10 })
    await fire(surface, 'pointermove', { clientX: 10, clientY: 90 })
    await vi.advanceTimersByTimeAsync(300)
    expect(video.playbackRate).toBe(1)

    // And the drag must not read as a tap either.
    await fire(surface, 'pointerup', { clientX: 10, clientY: 90 })
    expect(mediaPaused).toBe(false)
  })

  it('ends a skim when the browser takes the gesture over for a scroll', async () => {
    const { surface, video } = await mountHeld()

    await fire(surface, 'pointerdown', { clientX: 10, clientY: 10 })
    await vi.advanceTimersByTimeAsync(300)
    expect(video.playbackRate).toBe(2)

    await fire(surface, 'pointercancel')
    expect(video.playbackRate).toBe(1)
  })

  it('does not let a skim survive scrolling to the next reel', async () => {
    const { wrapper, surface, video } = await mountHeld()

    await fire(surface, 'pointerdown', { clientX: 10, clientY: 10 })
    await vi.advanceTimersByTimeAsync(300)
    expect(video.playbackRate).toBe(2)

    await wrapper.setProps({ isActive: false })
    await vi.runOnlyPendingTimersAsync()
    expect(video.playbackRate).toBe(1)
  })

  it('leaves a paused clip alone when it is held', async () => {
    const { surface, video } = await mountHeld()

    await tap(surface, 1, 1)
    expect(mediaPaused).toBe(true)

    await fire(surface, 'pointerdown', { clientX: 1, clientY: 1 })
    await vi.advanceTimersByTimeAsync(300)
    expect(video.playbackRate).toBe(1)
  })
})

describe('ReelClip — black-bar reframing', () => {
  // The reels column, and a frame that reports 1920x1080.
  const SLOT_W = 400
  const SLOT_H = 800

  beforeEach(() => {
    Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
      configurable: true,
      get: () => SLOT_W,
    })
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get: () => SLOT_H,
    })
  })

  afterEach(() => {
    // @ts-expect-error restoring jsdom's own accessors by deleting the overrides
    delete HTMLElement.prototype.clientWidth
    // @ts-expect-error see above
    delete HTMLElement.prototype.clientHeight
  })

  it('zooms past a pillarbox so the content fills the column width', async () => {
    detectPosterBars.mockResolvedValue({ x: 0.15, y: 0, width: 0.7, height: 1 })
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await flushPromises()

    // Content is 70% of a 400px-wide contain-fit box, so it takes 1/0.7 to fill the column.
    const style = wrapper.find('video').attributes('style')
    expect(style).toContain('scale(1.4286)')
  })

  it('re-centres content that sits off-centre between uneven bars', async () => {
    detectPosterBars.mockResolvedValue({ x: 0.3, y: 0, width: 0.6, height: 1 })
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await flushPromises()

    const style = wrapper.find('video').attributes('style')
    expect(style).toContain('scale(1.6667)')
    expect(style).toContain('translate(-16.6667%')
  })

  it('leaves the framing untouched when no bars are found', async () => {
    detectPosterBars.mockResolvedValue(null)
    const wrapper = await mountReel({ detail: makeDetail(), isActive: true })
    await flushPromises()
    expect(wrapper.find('video').attributes('style')).toBeUndefined()
  })

  // Detection is async and slots are recycled as the feed scrolls; a late answer must not be
  // applied to whatever clip now occupies the slot.
  it('ignores a detection that lands after the slot has swapped clips', async () => {
    let resolveStale: (rect: unknown) => void = () => {}
    detectPosterBars.mockImplementationOnce(
      () => new Promise((resolve) => (resolveStale = resolve)),
    )
    detectPosterBars.mockResolvedValue(null)

    const wrapper = await mountReel({
      clip: makeClip({ thumbnailUrl: 'https://cdn.test/a.jpg' }),
      detail: makeDetail(),
      isActive: true,
    })
    await wrapper.setProps({
      clip: makeClip({ id: 'clp_02', thumbnailUrl: 'https://cdn.test/b.jpg' }),
    })

    resolveStale({ x: 0.15, y: 0, width: 0.7, height: 1 })
    await flushPromises()
    expect(wrapper.find('video').attributes('style')).toBeUndefined()
  })
})
