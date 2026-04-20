import { describe, it, expect, beforeEach, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useThemeStore } from '../theme'
import { createLocalStorageMock, installLocalStorage, type MockLocalStorage } from '@/test/helpers'

let ls: MockLocalStorage

function stubMatchMedia(matches: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn(() => ({
      matches,
      addEventListener: () => {},
      removeEventListener: () => {},
    })),
  )
}

beforeEach(() => {
  ls = createLocalStorageMock()
  installLocalStorage(ls)
  document.documentElement.classList.remove('light')
  setActivePinia(createPinia())
  vi.unstubAllGlobals()
})

describe('useThemeStore', () => {
  it('defaults to dark when no stored preference and prefers-dark matches', () => {
    stubMatchMedia(true)
    const theme = useThemeStore()
    expect(theme.isDark).toBe(true)
  })

  it('respects a stored "light" preference over system', () => {
    ls.setItem('theme', 'light')
    stubMatchMedia(true)
    const theme = useThemeStore()
    expect(theme.isDark).toBe(false)
  })

  it('respects a stored "dark" preference', () => {
    ls.setItem('theme', 'dark')
    stubMatchMedia(false)
    const theme = useThemeStore()
    expect(theme.isDark).toBe(true)
  })

  it('falls back to dark when matchMedia is unavailable (e.g. older jsdom)', () => {
    // `window.matchMedia?.(...)` is optional-chained to survive environments without it.
    vi.stubGlobal('matchMedia', undefined)
    const theme = useThemeStore()
    expect(theme.isDark).toBe(true)
  })

  it('swallows localStorage read failures during init', () => {
    ls.__throwMode = true
    stubMatchMedia(true)
    // Should not throw; falls through to system preference.
    expect(() => useThemeStore()).not.toThrow()
  })

  it('toggle flips state, persists, and applies the .light class when not dark', () => {
    stubMatchMedia(true)
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
    stubMatchMedia(true)
    const theme = useThemeStore()
    ls.__throwMode = true
    expect(() => theme.toggle()).not.toThrow()
  })
})
