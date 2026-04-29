import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import ClipCard from '../ClipCard.vue'
import type { ClipFeedItem } from '@/api/clips'

function makeClip(overrides: Partial<ClipFeedItem> = {}): ClipFeedItem {
  return {
    id: 'clp_01',
    title: 'Unreal 1v5 clutch on Bind',
    description: null,
    thumbnailKey: null,
    durationSecs: 42,
    viewCount: 284_000,
    likeCount: 12_400,
    createdAt: new Date(Date.now() - 2 * 3600 * 1000).toISOString(),
    author: { id: 'u1', username: 'phantomveil', avatarUrl: null },
    game: { id: 1, name: 'Valorant', slug: 'valorant', tag: 'VALORANT' },
    likedByMe: false,
    ...overrides,
  }
}

describe('ClipCard', () => {
  it('renders the clip title', () => {
    const clip = makeClip()
    const wrapper = mount(ClipCard, { props: { clip } })
    expect(wrapper.text()).toContain(clip.title)
  })

  it('renders the game tag when game is set', () => {
    const wrapper = mount(ClipCard, { props: { clip: makeClip() } })
    expect(wrapper.text()).toContain('VALORANT')
  })

  it('omits the game tag when game is null', () => {
    const wrapper = mount(ClipCard, { props: { clip: makeClip({ game: null }) } })
    expect(wrapper.text()).not.toContain('VALORANT')
  })

  it('renders the formatted duration', () => {
    const wrapper = mount(ClipCard, { props: { clip: makeClip() } })
    expect(wrapper.text()).toContain('0:42')
  })

  it('renders the @username in neon', () => {
    const wrapper = mount(ClipCard, { props: { clip: makeClip() } })
    expect(wrapper.text()).toContain('@phantomveil')
  })

  it('emits click when the article is clicked', async () => {
    const wrapper = mount(ClipCard, { props: { clip: makeClip() } })
    await wrapper.find('article').trigger('click')
    expect(wrapper.emitted('click')).toBeTruthy()
  })
})
