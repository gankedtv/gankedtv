import { defineStore } from 'pinia'

function getInitialIsDark(): boolean {
  try {
    const stored = localStorage.getItem('theme')
    if (stored !== null) return stored !== 'light'
  } catch {}
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? true
}

export const useThemeStore = defineStore('theme', {
  state: () => ({
    isDark: getInitialIsDark(),
  }),
  actions: {
    toggle() {
      this.isDark = !this.isDark
      this.applyToDOM()
      try {
        localStorage.setItem('theme', this.isDark ? 'dark' : 'light')
      } catch {}
    },
    applyToDOM() {
      document.documentElement.classList.toggle('light', !this.isDark)
    },
  },
})
