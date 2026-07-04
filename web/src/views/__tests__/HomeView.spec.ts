import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { createRouter, createMemoryHistory, type Router } from 'vue-router'
import { createPinia } from 'pinia'
import { defineComponent, h, nextTick } from 'vue'
import type { ClipFeedItem, ClipFeedPage } from '@/api/clips'
import LoadMoreButton from '@/components/LoadMoreButton.vue'

const feed = vi.fn()
const featured = vi.fn()
vi.mock('@/api/clips', async () => {
  const actual = await vi.importActual<typeof import('@/api/clips')>('@/api/clips')
  return {
    ...actual,
    clips: {
      ...actual.clips,
      feed: (q?: unknown) => feed(q),
      featured: () => featured(),
    },
  }
})

const gamesList = vi.fn()
vi.mock('@/api/games', async () => {
  const actual = await vi.importActual<typeof import('@/api/games')>('@/api/games')
  return {
    ...actual,
    games: {
      ...actual.games,
      list: (...args: unknown[]) => gamesList(...args),
    },
  }
})

import HomeView from '../HomeView.vue'
import type { GameListItem } from '@/api/games'

beforeEach(() => {
  feed.mockReset()
  featured.mockReset()
  gamesList.mockReset()
  // Default: no pill games → no pill row, so the existing feed tests are unaffected.
  gamesList.mockResolvedValue([])
})

function makeGame(id: number, name: string, slug: string): GameListItem {
  return { id, name, slug, tag: name.toUpperCase(), coverUrl: null }
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

function makePage(items: ClipFeedItem[], nextCursor: string | null = null): ClipFeedPage {
  return { items, nextCursor }
}

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
      { path: '/trending', name: 'trending', component: stub },
      { path: '/games', name: 'games', component: stub },
      { path: '/game/:slug', name: 'game-detail', component: stub },
      { path: '/feed/reels', name: 'reels', component: stub },
    ],
  })
}

async function mountHome(): Promise<VueWrapper> {
  const router = makeRouter()
  await router.push('/')
  await router.isReady()
  const wrapper = mount(HomeView, { global: { plugins: [router, createPinia()] } })
  await flushPromises()
  return wrapper
}

// Text of the feed bands below the hero (the Latest Drops ranked list + The
// Feed grid). Scoping to the testids isolates the non-hero items cleanly.
function bandText(wrapper: VueWrapper): string {
  return wrapper
    .findAll('[data-testid="latest-drops"], [data-testid="feed-grid"]')
    .map((g) => g.text())
    .join(' ')
}

describe('HomeView — newest clip stays visible below the hero', () => {
  it('keeps the newest clip in the bands when a Clip of the Day is featured', async () => {
    // items[0] (the newest clip) is A; the featured pick X is a different, older
    // clip chosen by engagement, so it does not sit at items[0].
    feed.mockResolvedValue(
      makePage([
        makeClip('a', { title: 'Newest Clip' }),
        makeClip('b'),
        makeClip('c'),
        makeClip('d'),
        makeClip('e'),
      ]),
    )
    featured.mockResolvedValue(makeClip('x', { title: 'Featured Pick' }))

    const wrapper = await mountHome()

    // Hero is the featured pick…
    expect(wrapper.text()).toContain('Clip of the Day')
    expect(wrapper.text()).toContain('Featured Pick')
    // …and the newest clip must still render in the bands below it.
    expect(bandText(wrapper)).toContain('Newest Clip')
  })

  it('does not render the featured clip twice when it is also in the feed page', async () => {
    feed.mockResolvedValue(
      makePage([
        makeClip('a', { title: 'Newest Clip' }),
        makeClip('x', { title: 'Featured Pick' }),
        makeClip('c'),
        makeClip('d'),
      ]),
    )
    featured.mockResolvedValue(makeClip('x', { title: 'Featured Pick' }))

    const wrapper = await mountHome()

    expect(bandText(wrapper)).toContain('Newest Clip')
    // The featured clip is the hero; it must not also appear in the bands below.
    expect(bandText(wrapper)).not.toContain('Featured Pick')
  })

  it('keeps the newest clip in the bands after loading more older clips', async () => {
    // First page is newest-first with a cursor so "Load more" is offered.
    feed.mockResolvedValueOnce(
      makePage([makeClip('a', { title: 'Newest Clip' }), makeClip('b'), makeClip('c')], 'cursor-1'),
    )
    featured.mockResolvedValue(makeClip('x', { title: 'Featured Pick' }))

    const wrapper = await mountHome()
    expect(bandText(wrapper)).toContain('Newest Clip')

    // Loading more appends OLDER clips; the newest must stay in the bands.
    feed.mockResolvedValueOnce(makePage([makeClip('d', { title: 'Older Clip' }), makeClip('e')]))
    wrapper.findComponent(LoadMoreButton).vm.$emit('load')
    await flushPromises()

    expect(bandText(wrapper)).toContain('Newest Clip')
    expect(bandText(wrapper)).toContain('Older Clip')
  })

  it('falls back to the newest clip as hero and lists the rest when there is no featured pick', async () => {
    feed.mockResolvedValue(
      makePage([
        makeClip('a', { title: 'Newest Clip' }),
        makeClip('b', { title: 'Second Clip' }),
        makeClip('c', { title: 'Third Clip' }),
      ]),
    )
    featured.mockResolvedValue(null)

    const wrapper = await mountHome()

    // Hero falls back to the newest clip, labelled as a plain featured clip.
    expect(wrapper.text()).toContain('Featured Clip')
    expect(wrapper.text()).toContain('Newest Clip')
    // The hero clip is not duplicated in the bands; the rest are listed there.
    expect(bandText(wrapper)).not.toContain('Newest Clip')
    expect(bandText(wrapper)).toContain('Second Clip')
    expect(bandText(wrapper)).toContain('Third Clip')
  })
})

describe('HomeView — game filter pills', () => {
  // Mount at an arbitrary path and expose the router so tests can assert URL sync and
  // seed a ?game= deep-link.
  async function mountHomeAt(path = '/'): Promise<{ wrapper: VueWrapper; router: Router }> {
    const router = makeRouter()
    await router.push(path)
    await router.isReady()
    const wrapper = mount(HomeView, { global: { plugins: [router, createPinia()] } })
    await flushPromises()
    return { wrapper, router }
  }

  function pillRow(wrapper: VueWrapper) {
    return wrapper.find('[aria-label="Filter by game"]')
  }

  function findPill(wrapper: VueWrapper, label: string) {
    return pillRow(wrapper)
      .findAll('button')
      .find((b) => b.text() === label)
  }

  // The feed mock is shared by the main feed (loadMore, limit 20) and the trending band
  // (loadBandTrending, limit 5), so match the feed-load call on its limit to disambiguate.
  const feedLoad = (extra: Record<string, unknown>) =>
    expect.objectContaining({ limit: 20, ...extra })

  it('renders an All + per-game pill row (game tags) from games.list', async () => {
    gamesList.mockResolvedValue([makeGame(2, 'Valorant', 'valorant'), makeGame(5, 'Apex', 'apex')])
    feed.mockResolvedValue(makePage([makeClip('a')]))
    featured.mockResolvedValue(null)

    const { wrapper } = await mountHomeAt()

    // Only watchable games are offered as pills.
    expect(gamesList).toHaveBeenCalledWith(expect.any(Number), { hasClips: true })
    const row = pillRow(wrapper)
    expect(row.exists()).toBe(true)
    expect(row.text()).toContain('All')
    expect(row.text()).toContain('VALORANT')
    expect(row.text()).toContain('APEX')
    // No filter active on first load → All is the pressed pill.
    expect(findPill(wrapper, 'All')?.attributes('aria-pressed')).toBe('true')
  })

  it('does not render a pill row when there are no watchable games', async () => {
    gamesList.mockResolvedValue([])
    feed.mockResolvedValue(makePage([makeClip('a')]))
    featured.mockResolvedValue(null)

    const { wrapper } = await mountHomeAt()

    expect(pillRow(wrapper).exists()).toBe(false)
  })

  it('filters the feed in place with the gameId when a pill is clicked', async () => {
    gamesList.mockResolvedValue([makeGame(2, 'Valorant', 'valorant'), makeGame(5, 'Apex', 'apex')])
    feed.mockResolvedValue(makePage([makeClip('a', { title: 'All Clip' })]))
    featured.mockResolvedValue(null)

    const { wrapper, router } = await mountHomeAt()
    // The mount feed load is unfiltered.
    expect(feed).toHaveBeenCalledWith(feedLoad({ gameId: undefined }))

    feed.mockResolvedValue(makePage([makeClip('v', { title: 'Valorant Clip' })]))
    await findPill(wrapper, 'VALORANT')!.trigger('click')
    await flushPromises()

    // Feed re-fetched scoped to the game, and the active pill + URL reflect it.
    expect(feed).toHaveBeenCalledWith(feedLoad({ gameId: 2, cursor: null }))
    expect(findPill(wrapper, 'VALORANT')?.attributes('aria-pressed')).toBe('true')
    expect(findPill(wrapper, 'All')?.attributes('aria-pressed')).toBe('false')
    expect(router.currentRoute.value.query.game).toBe('valorant')
  })

  it('resets to the unfiltered feed when All is clicked', async () => {
    gamesList.mockResolvedValue([makeGame(2, 'Valorant', 'valorant')])
    feed.mockResolvedValue(makePage([makeClip('a')]))
    featured.mockResolvedValue(null)

    const { wrapper, router } = await mountHomeAt()
    await findPill(wrapper, 'VALORANT')!.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query.game).toBe('valorant')

    feed.mockClear()
    await findPill(wrapper, 'All')!.trigger('click')
    await flushPromises()

    expect(feed).toHaveBeenCalledWith(feedLoad({ gameId: undefined }))
    expect(findPill(wrapper, 'All')?.attributes('aria-pressed')).toBe('true')
    // The stale ?game= param is dropped from the URL.
    expect(router.currentRoute.value.query.game).toBeUndefined()
  })

  it('initializes the filter from the ?game= query on mount', async () => {
    gamesList.mockResolvedValue([makeGame(2, 'Valorant', 'valorant'), makeGame(5, 'Apex', 'apex')])
    feed.mockResolvedValue(makePage([makeClip('v')]))
    featured.mockResolvedValue(null)

    const { wrapper } = await mountHomeAt('/?game=apex')

    // The first feed load is already scoped to the deep-linked game — no unfiltered flash.
    expect(feed).toHaveBeenCalledWith(feedLoad({ gameId: 5 }))
    expect(feed).not.toHaveBeenCalledWith(feedLoad({ gameId: undefined }))
    expect(findPill(wrapper, 'APEX')?.attributes('aria-pressed')).toBe('true')
  })

  it('falls back to the unfiltered feed when ?game= is not a watchable game', async () => {
    gamesList.mockResolvedValue([makeGame(2, 'Valorant', 'valorant')])
    feed.mockResolvedValue(makePage([makeClip('a')]))
    featured.mockResolvedValue(null)

    const { wrapper, router } = await mountHomeAt('/?game=nonexistent')

    expect(feed).toHaveBeenCalledWith(feedLoad({ gameId: undefined }))
    expect(findPill(wrapper, 'All')?.attributes('aria-pressed')).toBe('true')
    expect(router.currentRoute.value.query.game).toBeUndefined()
  })

  it('hides the Top Games discovery band while a game filter is active', async () => {
    gamesList.mockResolvedValue([makeGame(2, 'Valorant', 'valorant')])
    feed.mockResolvedValue(makePage([makeClip('a'), makeClip('b'), makeClip('c')]))
    featured.mockResolvedValue(null)

    const { wrapper } = await mountHomeAt()
    // The "Top Games" band header only appears in the discovery band, not the pill row.
    expect(wrapper.text()).toContain('Top Games')

    await findPill(wrapper, 'VALORANT')!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).not.toContain('Top Games')
  })

  it('shows the loading state (not the empty state) while a deep-linked ?game= resolves', async () => {
    // Hold games.list open so the deep-link branch is mid-resolve when we inspect the DOM.
    let resolveGames!: (g: GameListItem[]) => void
    gamesList.mockReturnValue(
      new Promise<GameListItem[]>((r) => {
        resolveGames = r
      }),
    )
    feed.mockResolvedValue(makePage([makeClip('v', { title: 'Valorant Clip' })]))
    featured.mockResolvedValue(null)

    const router = makeRouter()
    await router.push('/?game=valorant')
    await router.isReady()
    const wrapper = mount(HomeView, { global: { plugins: [router, createPinia()] } })
    await nextTick()

    // bandGames still pending → loading panel up, no misleading "no content" panel, and the
    // game-scoped feed load hasn't been dispatched yet (only the trending band's limit-5 call).
    expect(wrapper.text()).toContain('Loading')
    expect(wrapper.text()).not.toContain('No clips')
    expect(feed).not.toHaveBeenCalledWith(expect.objectContaining({ limit: 20 }))

    resolveGames([makeGame(2, 'Valorant', 'valorant')])
    await flushPromises()

    // Resolved → the first main feed load is already game-scoped.
    expect(feed).toHaveBeenCalledWith(feedLoad({ gameId: 2 }))
  })

  it('shows a game-filter empty state with a Clear filter action when the filtered feed is empty', async () => {
    gamesList.mockResolvedValue([makeGame(2, 'Valorant', 'valorant')])
    featured.mockResolvedValue(null)
    // Unfiltered feed has clips; the Valorant filter legitimately returns nothing.
    feed.mockImplementation((q?: { gameId?: number }) =>
      Promise.resolve(makePage(q?.gameId === 2 ? [] : [makeClip('a')])),
    )

    const { wrapper, router } = await mountHomeAt()
    await findPill(wrapper, 'VALORANT')!.trigger('click')
    await flushPromises()

    // Dedicated message, not the generic "be the first" / Following panels.
    expect(wrapper.text()).toContain('No clips for Valorant yet.')
    expect(wrapper.text()).not.toContain('be the first')

    const clear = wrapper.findAll('button').find((b) => b.text() === 'Clear filter')
    await clear!.trigger('click')
    await flushPromises()

    // Clearing resets to All and the unfiltered feed returns.
    expect(router.currentRoute.value.query.game).toBeUndefined()
    expect(findPill(wrapper, 'All')?.attributes('aria-pressed')).toBe('true')
    expect(wrapper.text()).not.toContain('No clips for Valorant yet.')
  })
})
