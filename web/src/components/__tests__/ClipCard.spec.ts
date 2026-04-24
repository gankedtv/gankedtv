import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import ClipCard from '../ClipCard.vue'
import { CLIPS } from '@/lib/mock-data'

const clip = CLIPS[0] // Unreal 1v5 clutch — valorant / phantomveil

describe('ClipCard', () => {
  it('renders the clip title', () => {
    const wrapper = mount(ClipCard, { props: { clip } })
    expect(wrapper.text()).toContain(clip.title)
  })

  it('renders the game tag', () => {
    const wrapper = mount(ClipCard, { props: { clip } })
    expect(wrapper.text()).toContain('VALORANT')
  })

  it('renders the formatted duration', () => {
    const wrapper = mount(ClipCard, { props: { clip } })
    // duration 42s → "0:42"
    expect(wrapper.text()).toContain('0:42')
  })

  it('renders the @username in neon', () => {
    const wrapper = mount(ClipCard, { props: { clip } })
    expect(wrapper.text()).toContain('@phantomveil')
  })

  it('emits click when the article is clicked', async () => {
    const wrapper = mount(ClipCard, { props: { clip } })
    await wrapper.find('article').trigger('click')
    expect(wrapper.emitted('click')).toBeTruthy()
  })
})
