import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import UserAvatar from '../UserAvatar.vue'

describe('UserAvatar', () => {
  it('renders initials from the username when no avatarUrl', () => {
    const wrapper = mount(UserAvatar, {
      props: { user: { username: 'phantomveil', avatarUrl: null } },
    })
    expect(wrapper.text()).toBe('PH')
  })

  it("falls back to '??' when the username has no letters", () => {
    const wrapper = mount(UserAvatar, {
      props: { user: { username: '123-456', avatarUrl: null } },
    })
    expect(wrapper.text()).toBe('??')
  })

  it('renders an <img> when avatarUrl is provided', () => {
    const wrapper = mount(UserAvatar, {
      props: { user: { username: 'zoe', avatarUrl: 'https://cdn/x.png' } },
    })
    const img = wrapper.find('img')
    expect(img.exists()).toBe(true)
    expect(img.attributes('src')).toBe('https://cdn/x.png')
  })

  it('applies the requested size', () => {
    const wrapper = mount(UserAvatar, {
      props: { user: { username: 'nyxproto', avatarUrl: null }, size: 48 },
    })
    const el = wrapper.element as HTMLElement
    expect(el.style.width).toBe('48px')
    expect(el.style.height).toBe('48px')
  })

  it('defaults to size 32', () => {
    const wrapper = mount(UserAvatar, {
      props: { user: { username: 'sundownr', avatarUrl: null } },
    })
    const el = wrapper.element as HTMLElement
    expect(el.style.width).toBe('32px')
  })
})
