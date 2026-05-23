import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { defineComponent, h } from 'vue'
import TagChip from '../TagChip.vue'

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: defineComponent({ render: () => h('div') }) },
      {
        path: '/tag/:slug',
        name: 'tag-detail',
        component: defineComponent({ render: () => h('div') }),
      },
    ],
  })
}

describe('TagChip', () => {
  it('renders as a RouterLink to /tag/:slug by default', async () => {
    const router = makeRouter()
    await router.push('/')
    await router.isReady()
    const wrapper = mount(TagChip, {
      props: { slug: 'clutch', name: 'clutch' },
      global: { plugins: [router] },
    })
    const link = wrapper.find('a')
    expect(link.exists()).toBe(true)
    expect(link.attributes('href')).toBe('/tag/clutch')
    expect(link.text()).toContain('clutch')
  })

  it('renders a non-link span when interactive=false', async () => {
    const router = makeRouter()
    await router.push('/')
    await router.isReady()
    const wrapper = mount(TagChip, {
      props: { slug: 'clutch', name: '+2', interactive: false },
      global: { plugins: [router] },
    })
    expect(wrapper.find('a').exists()).toBe(false)
    expect(wrapper.find('span').text()).toBe('+2')
  })

  it('encodes the slug into the route', async () => {
    const router = makeRouter()
    await router.push('/')
    await router.isReady()
    const wrapper = mount(TagChip, {
      props: { slug: 'with-hyphen', name: 'with-hyphen' },
      global: { plugins: [router] },
    })
    expect(wrapper.find('a').attributes('href')).toBe('/tag/with-hyphen')
  })
})
