import { defineStore } from 'pinia'

export const useThemeStore = defineStore('theme', {
  state: () => ({
    isDark: localStorage.getItem('theme') !== 'light',
  }),
  actions: {
    toggle() {
      this.isDark = !this.isDark
      this.applyToDOM()
    },
    applyToDOM() {
      document.documentElement.classList.toggle('light', !this.isDark)
      localStorage.setItem('theme', this.isDark ? 'dark' : 'light')
    },
  },
})
