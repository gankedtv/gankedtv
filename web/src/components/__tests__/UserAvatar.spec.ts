import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import UserAvatar from '../UserAvatar.vue'

describe('UserAvatar', () => {
  it('renders initials for a known user', () => {
    const wrapper = mount(UserAvatar, { props: { user: 'phantomveil' } })
    expect(wrapper.text()).toBe('PH')
  })

  it('renders initials for an unknown user key (falls back to display prop)', () => {
    const wrapper = mount(UserAvatar, { props: { user: 'unknownkey' } })
    // falls back to { display: 'unknownkey', avatar: '#6d28d9' } → 'UN'
    expect(wrapper.text()).toBe('UN')
  })

  it('applies the requested size', () => {
    const wrapper = mount(UserAvatar, { props: { user: 'nyxproto', size: 48 } })
    const el = wrapper.element as HTMLElement
    expect(el.style.width).toBe('48px')
    expect(el.style.height).toBe('48px')
  })

  it('defaults to size 32', () => {
    const wrapper = mount(UserAvatar, { props: { user: 'sundownr' } })
    const el = wrapper.element as HTMLElement
    expect(el.style.width).toBe('32px')
  })
})
