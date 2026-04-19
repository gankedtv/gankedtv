import { defineStore } from 'pinia'
import type { MeResponse } from '@/api/auth'

const REFRESH_KEY = 'refresh_token'

function loadRefreshFromLocalStorage(): string | null {
  if (import.meta.env.VITE_USE_SECURE_COOKIES === 'true') {
    return null
  }
  try {
    return localStorage.getItem(REFRESH_KEY)
  } catch {
    return null
  }
}

function persistRefresh(token: string | null): void {
  // WARNING: Storing refresh tokens in localStorage is susceptible to XSS attacks.
  // We intentionally accept this risk for now. Setting VITE_USE_SECURE_COOKIES=true
  // will stop writing to localStorage (this requires backend modifications to send the Secure, HttpOnly cookie).
  if (import.meta.env.VITE_USE_SECURE_COOKIES === 'true') {
    // TODO: POST the token to an endpoint that sets the HttpOnly cookie.
    // e.g., fetch('/api/auth/refresh-cookie', { method: 'POST', body: JSON.stringify({ token }) })
    return
  }

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
      // Lazy import to avoid circular dependency (router → store → router).
      // isReady() defers navigation until the router is installed (logout can fire during bootstrap).
      import('@/router')
        .then(({ default: router }) => router.isReady().then(() => router.push({ name: 'login' })))
        .catch((err) => {
          console.error('logout navigation failed', err)
        })
    },
  },
})
