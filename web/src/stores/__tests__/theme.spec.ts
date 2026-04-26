import { describe, it, expect, beforeEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useThemeStore, DEFAULT_THEME } from '../theme'
import { createLocalStorageMock, installLocalStorage, type MockLocalStorage } from '@/test/helpers'

let ls: MockLocalStorage

beforeEach(() => {
  ls = createLocalStorageMock()
  installLocalStorage(ls)
  document.documentElement.classList.remove('light')
  document.documentElement.removeAttribute('data-theme')
  setActivePinia(createPinia())
})

describe('useThemeStore', () => {
  it('defaults to dark when no stored preference exists', () => {
    const theme = useThemeStore()
    expect(theme.isDark).toBe(true)
  })

  it('respects a stored "light" preference', () => {
    ls.setItem('theme', 'light')
    const theme = useThemeStore()
    expect(theme.isDark).toBe(false)
  })

  it('respects a stored "dark" preference', () => {
    ls.setItem('theme', 'dark')
    const theme = useThemeStore()
    expect(theme.isDark).toBe(true)
  })

  it('swallows localStorage read failures during init', () => {
    ls.__throwMode = true
    expect(() => useThemeStore()).not.toThrow()
  })

  it('toggle flips state, persists, and applies the .light class when not dark', () => {
    const theme = useThemeStore()
    expect(theme.isDark).toBe(true)

    theme.toggle()
    expect(theme.isDark).toBe(false)
    expect(document.documentElement.classList.contains('light')).toBe(true)
    expect(ls.getItem('theme')).toBe('light')

    theme.toggle()
    expect(theme.isDark).toBe(true)
    expect(document.documentElement.classList.contains('light')).toBe(false)
    expect(ls.getItem('theme')).toBe('dark')
  })

  it('toggle swallows localStorage write failures', () => {
    const theme = useThemeStore()
    ls.__throwMode = true
    expect(() => theme.toggle()).not.toThrow()
  })

  it('defaults to the configured default theme when nothing is stored', () => {
    const theme = useThemeStore()
    expect(theme.name).toBe(DEFAULT_THEME)
  })

  it('reads a stored theme name when valid', () => {
    ls.setItem('theme:name', 'tactical')
    const theme = useThemeStore()
    expect(theme.name).toBe('tactical')
  })

  it('falls back to the default theme when the stored value is invalid', () => {
    ls.setItem('theme:name', 'not-a-real-theme')
    const theme = useThemeStore()
    expect(theme.name).toBe(DEFAULT_THEME)
  })

  it('swallows localStorage read failures while resolving theme name', () => {
    ls.__throwMode = true
    expect(() => useThemeStore()).not.toThrow()
  })

  it('setName updates state, stamps data-theme, and persists', () => {
    const theme = useThemeStore()

    theme.setName('underground')
    expect(theme.name).toBe('underground')
    expect(document.documentElement.getAttribute('data-theme')).toBe('underground')
    expect(ls.getItem('theme:name')).toBe('underground')

    theme.setName('tactical')
    expect(theme.name).toBe('tactical')
    expect(document.documentElement.getAttribute('data-theme')).toBe('tactical')
    expect(ls.getItem('theme:name')).toBe('tactical')
  })

  it('setName swallows localStorage write failures', () => {
    const theme = useThemeStore()
    ls.__throwMode = true
    expect(() => theme.setName('arcade')).not.toThrow()
  })

  it('applyToDOM stamps both the .light class and the data-theme attribute', () => {
    ls.setItem('theme', 'light')
    ls.setItem('theme:name', 'tactical')
    const theme = useThemeStore()

    theme.applyToDOM()
    expect(document.documentElement.classList.contains('light')).toBe(true)
    expect(document.documentElement.getAttribute('data-theme')).toBe('tactical')
  })
})
