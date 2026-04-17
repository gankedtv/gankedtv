import { defineStore } from 'pinia'

interface User {
  id: string
  username: string
  email: string | null
  avatarUrl: string | null
  bio: string | null
}

export const useAuthStore = defineStore('auth', {
  state: () => ({
    user: null as User | null,
    accessToken: null as string | null,
  }),
  getters: {
    isAuthenticated: (state): boolean => !!state.user,
  },
  actions: {
    setUser(user: User, token: string) {
      this.user = user
      this.accessToken = token
    },
    logout() {
      this.user = null
      this.accessToken = null
    },
  },
})
