import { defineStore } from 'pinia'
import { ApiError, bumpSessionEpoch } from '@/api/client'
import { config } from '@/config'
import type { MeResponse } from '@/api/auth'

const REFRESH_KEY = 'refresh_token'

function loadRefreshFromLocalStorage(): string | null {
  if (config.useSecureCookies) {
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
  // Cookie mode (VITE_USE_SECURE_COOKIES=true + the server's AUTH_REFRESH_COOKIE_ENABLED)
  // avoids that: the server keeps the token in an HttpOnly cookie and nothing is
  // persisted here — we only clear a stale key left over from a localStorage deploy.
  if (config.useSecureCookies) {
    if (token === null) {
      try {
        localStorage.removeItem(REFRESH_KEY)
      } catch {}
    }
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
    // Admin is a strict superset of moderator — every admin endpoint a mod can hit, the
    // admin can hit too, plus the user-disable ones. The two getters mirror the server
    // policy registration in RoleAuthorization.AddRolePolicies so the UI never offers a
    // path the server would 403.
    isAdmin: (state): boolean => state.user?.role === 'admin',
    isModerator: (state): boolean =>
      state.user?.role === 'admin' || state.user?.role === 'moderator',
  },

  actions: {
    setSession(token: string, refresh: string) {
      bumpSessionEpoch()
      this.accessToken = token
      this.refreshToken = refresh || null
      persistRefresh(refresh || null)
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
      // Cookie mode always attempts /me: the credential is the HttpOnly cookie, which the
      // client can't see — the api client's 401-refresh-retry path exercises it.
      if (!this.refreshToken && !config.useSecureCookies) return
      try {
        await this.fetchMe()
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          this.user = null
          this.accessToken = null
          this.refreshToken = null
          persistRefresh(null)
        } else {
          throw err
        }
      }
    },

    logout() {
      const refresh = this.refreshToken
      // Fire-and-forget server revocation (and cookie clearing in cookie mode) — local
      // state is cleared regardless, so a network failure still logs the client out.
      import('@/api/auth').then(({ logout: serverLogout }) => serverLogout(refresh)).catch(() => {})
      bumpSessionEpoch()
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
