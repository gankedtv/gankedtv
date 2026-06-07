import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { createRouter, createMemoryHistory, type Router } from 'vue-router'
import { createPinia } from 'pinia'
import { defineComponent, h } from 'vue'
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

import HomeView from '../HomeView.vue'

beforeEach(() => {
  feed.mockReset()
  featured.mockReset()
})

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

// Text of the two feed bands below the hero (Latest Drops + main grid). The hero
// renders outside any .feed-grid (and is duplicated across the desktop/mobile
// breakpoints), so scoping to .feed-grid isolates the non-hero items cleanly.
function bandText(wrapper: VueWrapper): string {
  return wrapper
    .findAll('.feed-grid')
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
