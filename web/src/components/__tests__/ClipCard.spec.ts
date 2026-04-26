import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import ClipCard from '../ClipCard.vue'
import { CLIPS } from '@/lib/mock-data'

// Look up by id so this test isn't sensitive to the order of the mock fixture
const clip = CLIPS.find((c) => c.id === 'clp_01')
if (!clip) throw new Error('Test fixture clip clp_01 not found in CLIPS mock data')

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
