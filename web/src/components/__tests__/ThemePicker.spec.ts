import { describe, it, expect, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import ThemePicker from '../ThemePicker.vue'
import { useThemeStore } from '@/stores/theme'
import { createLocalStorageMock, installLocalStorage } from '@/test/helpers'

beforeEach(() => {
  installLocalStorage(createLocalStorageMock())
  document.documentElement.removeAttribute('data-theme')
  setActivePinia(createPinia())
})

describe('ThemePicker', () => {
  it('renders one button per theme', () => {
    const wrapper = mount(ThemePicker)
    expect(wrapper.findAll('button')).toHaveLength(3)
  })

  it('marks the currently-active theme with aria-pressed', () => {
    const theme = useThemeStore()
    theme.setName('tactical')

    const wrapper = mount(ThemePicker)
    const tacticalBtn = wrapper.get('button[aria-label="Switch to tactical theme"]')
    const arcadeBtn = wrapper.get('button[aria-label="Switch to arcade theme"]')

    expect(tacticalBtn.attributes('aria-pressed')).toBe('true')
    expect(arcadeBtn.attributes('aria-pressed')).toBe('false')
  })

  it('switches the active theme in the store on click', async () => {
    const theme = useThemeStore()
    const wrapper = mount(ThemePicker)

    await wrapper.get('button[aria-label="Switch to underground theme"]').trigger('click')

    expect(theme.name).toBe('underground')
    expect(document.documentElement.getAttribute('data-theme')).toBe('underground')
  })
})
