import { defineStore } from 'pinia'

export type ThemeName = 'underground' | 'tactical' | 'arcade'

export const THEME_NAMES: readonly ThemeName[] = ['underground', 'tactical', 'arcade'] as const
export const DEFAULT_THEME: ThemeName = 'arcade'

const THEME_STORAGE_KEY = 'theme:name'

function isThemeName(value: unknown): value is ThemeName {
  return typeof value === 'string' && (THEME_NAMES as readonly string[]).includes(value)
}

function getInitialIsDark(): boolean {
  try {
    const stored = localStorage.getItem('theme')
    if (stored !== null) return stored !== 'light'
  } catch {}
  return true
}

function getInitialThemeName(): ThemeName {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY)
    if (isThemeName(stored)) return stored
  } catch {}
  return DEFAULT_THEME
}

export const useThemeStore = defineStore('theme', {
  state: () => ({
    isDark: getInitialIsDark(),
    name: getInitialThemeName(),
  }),
  actions: {
    toggle() {
      this.isDark = !this.isDark
      this.applyToDOM()
      try {
        localStorage.setItem('theme', this.isDark ? 'dark' : 'light')
      } catch {}
    },
    setName(name: ThemeName) {
      this.name = name
      this.applyToDOM()
      try {
        localStorage.setItem(THEME_STORAGE_KEY, name)
      } catch {}
    },
    applyToDOM() {
      document.documentElement.classList.toggle('light', !this.isDark)
      document.documentElement.setAttribute('data-theme', this.name)
    },
  },
})
