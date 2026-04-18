import { defineStore } from 'pinia'
import type { MeResponse } from '@/api/auth'

const REFRESH_KEY = 'refresh_token'

function loadRefreshFromLocalStorage(): string | null {
  try {
    return localStorage.getItem(REFRESH_KEY)
  } catch {
    return null
  }
}

function persistRefresh(token: string | null): void {
  try {
    if (token) {
      localStorage.setItem(REFRESH_KEY, token)
    } else {
      localStorage.removeItem(REFRESH_KEY)
    }
  } catch {}
}

export const useAuthStore = defineStore('auth', {
  state: () => ({
    user: null as MeResponse | null,
    accessToken: null as string | null,
    refreshToken: loadRefreshFromLocalStorage(),
    bootstrapped: false,
  }),

  getters: {
    isAuthenticated: (state): boolean => !!state.user,
  },

  actions: {
    setSession(token: string, refresh: string) {
      this.accessToken = token
      this.refreshToken = refresh
      persistRefresh(refresh)
    },

    setUser(user: MeResponse) {
      this.user = user
    },

    async fetchMe() {
      const { me } = await import('@/api/auth')
      const user = await me()
      this.user = user
    },

    // Called once at app boot. If a refresh token is persisted, /me is fetched.
    // The api client handles the 401→refresh→retry cycle automatically, so bootstrap
    // just needs to trigger the /me call.
    async bootstrap() {
      if (this.bootstrapped) return
      this.bootstrapped = true
      if (!this.refreshToken) return
      try {
        await this.fetchMe()
      } catch {
        this.user = null
        this.accessToken = null
        this.refreshToken = null
        persistRefresh(null)
      }
    },

    logout() {
      this.user = null
      this.accessToken = null
      this.refreshToken = null
      this.bootstrapped = false
      persistRefresh(null)
      // Lazy import to avoid circular dependency (router → store → router)
      import('@/router').then(({ default: router }) => {
        router.push({ name: 'login' })
      })
    },
  },
})
