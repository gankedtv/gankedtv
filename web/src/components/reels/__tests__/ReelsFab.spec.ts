import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { defineComponent, h } from 'vue'
import ReelsFab from '../ReelsFab.vue'

function makeRouter() {
  const stub = defineComponent({ render: () => h('div') })
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: stub },
      { path: '/feed/reels', name: 'reels', component: stub },
    ],
  })
}

describe('ReelsFab', () => {
  it('renders a router-link to the reels feed with an accessible label', () => {
    const wrapper = mount(ReelsFab, { global: { plugins: [makeRouter()] } })
    const link = wrapper.find('a')
    expect(link.exists()).toBe(true)
    expect(link.attributes('href')).toBe('/feed/reels')
    expect(link.attributes('aria-label')).toBe('Open reels feed')
  })
})
