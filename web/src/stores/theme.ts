import { defineStore } from 'pinia'

export type ThemeMode = 'dark' | 'light'

const MODE_STORAGE_KEY = 'theme'
// v1's three-theme system stored a name under this key. Newsprint has one
// palette, so the key is removed on init — users on 'tactical'/'arcade' land
// on Newsprint silently, keeping whatever dark/light mode they had.
const LEGACY_NAME_KEY = 'theme:name'

function getInitialMode(): ThemeMode {
  try {
    localStorage.removeItem(LEGACY_NAME_KEY)
  } catch {}
  try {
    const stored = localStorage.getItem(MODE_STORAGE_KEY)
    if (stored !== null) return stored === 'light' ? 'light' : 'dark'
  } catch {}
  return 'dark'
}

export const useThemeStore = defineStore('theme', {
  state: () => ({
    mode: getInitialMode(),
  }),
  getters: {
    isDark: (state): boolean => state.mode === 'dark',
  },
  actions: {
    toggle() {
      this.mode = this.mode === 'dark' ? 'light' : 'dark'
      this.applyToDOM()
      try {
        localStorage.setItem(MODE_STORAGE_KEY, this.mode)
      } catch {}
    },
    applyToDOM() {
      document.documentElement.classList.toggle('light', this.mode === 'light')
      // Scrub the v1 attribute so stale [data-theme] markup can't linger across
      // a deploy boundary for returning users.
      document.documentElement.removeAttribute('data-theme')
    },
  },
})
