import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { defineComponent, h } from 'vue'
import ClipCard from '../ClipCard.vue'
import type { ClipFeedItem } from '@/api/clips'

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: defineComponent({ render: () => h('div') }) },
      {
        path: '/game/:slug',
        name: 'game-detail',
        component: defineComponent({ render: () => h('div') }),
      },
      {
        path: '/tag/:slug',
        name: 'tag-detail',
        component: defineComponent({ render: () => h('div') }),
      },
    ],
  })
}

function makeClip(overrides: Partial<ClipFeedItem> = {}): ClipFeedItem {
  return {
    id: 'clp_01',
    title: 'Unreal 1v5 clutch on Bind',
    description: null,
    thumbnailUrl: 'https://cdn.test/thumb.jpg',
    durationSecs: 42,
    viewCount: 284_000,
    likeCount: 12_400,
    createdAt: new Date(Date.now() - 2 * 3600 * 1000).toISOString(),
    author: { id: 'u1', username: 'phantomveil', avatarUrl: null },
    game: { id: 1, name: 'Valorant', slug: 'valorant', tag: 'VALORANT' },
    tags: [],
    likedByMe: false,
    shareCode: 'testcode',
    ...overrides,
  }
}

describe('ClipCard', () => {
  it('renders the clip title', () => {
    const clip = makeClip()
    const wrapper = mount(ClipCard, { props: { clip }, global: { plugins: [makeRouter()] } })
    expect(wrapper.text()).toContain(clip.title)
  })

  it('renders the game tag when game is set', () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip() },
      global: { plugins: [makeRouter()] },
    })
    expect(wrapper.text()).toContain('VALORANT')
  })

  it('omits the game tag when game is null', () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip({ game: null }) },
      global: { plugins: [makeRouter()] },
    })
    expect(wrapper.text()).not.toContain('VALORANT')
  })

  it('renders the formatted duration', () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip() },
      global: { plugins: [makeRouter()] },
    })
    expect(wrapper.text()).toContain('0:42')
  })

  it('renders the @username handle', () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip() },
      global: { plugins: [makeRouter()] },
    })
    expect(wrapper.text()).toContain('@phantomveil')
  })

  it('renders the thumbnail img bound to thumbnailUrl', () => {
    const clip = makeClip()
    const wrapper = mount(ClipCard, { props: { clip }, global: { plugins: [makeRouter()] } })
    expect(wrapper.find('img').attributes('src')).toBe(clip.thumbnailUrl)
  })

  it('hides the author row but keeps the relative time when showAuthor is false', () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip(), showAuthor: false },
      global: { plugins: [makeRouter()] },
    })
    expect(wrapper.text()).not.toContain('@phantomveil')
    expect(wrapper.text()).toContain('2h')
    expect(wrapper.find('img').attributes('src')).toBe('https://cdn.test/thumb.jpg')
  })

  it('emits click when the article is clicked', async () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip() },
      global: { plugins: [makeRouter()] },
    })
    await wrapper.find('article').trigger('click')
    expect(wrapper.emitted('click')).toBeTruthy()
  })

  it('renders the game tag as a link to /game/:slug', () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip() },
      global: { plugins: [makeRouter()] },
    })
    const link = wrapper.find('a')
    expect(link.exists()).toBe(true)
    expect(link.attributes('href')).toBe('/game/valorant')
  })

  it('does not emit card click when the game-tag link is clicked', async () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip() },
      global: { plugins: [makeRouter()] },
    })
    await wrapper.find('a').trigger('click')
    expect(wrapper.emitted('click')).toBeFalsy()
  })

  it('defers the thumbnail — lazy, async-decoded, and faded in over the placeholder', () => {
    // Every feed grid renders dozens of these below the fold; eager loading is what makes a
    // long feed slow as the catalogue grows (issue #199).
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip() },
      global: { plugins: [makeRouter()] },
    })

    const img = wrapper.find('img')
    expect(img.attributes('loading')).toBe('lazy')
    expect(img.attributes('decoding')).toBe('async')
    expect(img.classes()).toContain('opacity-0')
  })

  it('reveals the thumbnail once it loads', async () => {
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip() },
      global: { plugins: [makeRouter()] },
    })

    await wrapper.find('img').trigger('load')

    expect(wrapper.find('img').classes()).toContain('opacity-100')
  })

  it('does not render tag chips on the card — tags live on detail surfaces', () => {
    const tags = [
      { id: 1, slug: 'clutch', name: 'clutch', clipCount: 0 },
      { id: 2, slug: 'ace', name: 'ace', clipCount: 0 },
    ]
    const wrapper = mount(ClipCard, {
      props: { clip: makeClip({ tags }) },
      global: { plugins: [makeRouter()] },
    })
    const tagLinks = wrapper.findAll('a').filter((a) => a.attributes('href')?.startsWith('/tag/'))
    expect(tagLinks.length).toBe(0)
  })
})
