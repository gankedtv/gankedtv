import { describe, it, expect, beforeEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useThemeStore } from '../theme'
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
    expect(theme.mode).toBe('dark')
    expect(theme.isDark).toBe(true)
  })

  it('respects a stored "light" preference', () => {
    ls.setItem('theme', 'light')
    const theme = useThemeStore()
    expect(theme.mode).toBe('light')
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

  it('silently removes the legacy v1 theme-name key on init', () => {
    ls.setItem('theme:name', 'tactical')
    useThemeStore()
    expect(ls.getItem('theme:name')).toBeNull()
  })

  it('keeps the stored mode when migrating a legacy multi-theme user', () => {
    ls.setItem('theme:name', 'arcade')
    ls.setItem('theme', 'light')
    const theme = useThemeStore()
    expect(theme.mode).toBe('light')
    expect(ls.getItem('theme:name')).toBeNull()
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

  it('applyToDOM scrubs a stale v1 data-theme attribute', () => {
    document.documentElement.setAttribute('data-theme', 'arcade')
    const theme = useThemeStore()
    theme.applyToDOM()
    expect(document.documentElement.getAttribute('data-theme')).toBeNull()
    expect(document.documentElement.classList.contains('light')).toBe(false)
  })
})
