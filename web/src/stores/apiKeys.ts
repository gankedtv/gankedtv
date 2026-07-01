import { defineStore } from 'pinia'
import { apiKeys as api, type ApiKeyItem } from '@/api/apiKeys'
import { ApiError } from '@/api/client'

interface State {
  items: ApiKeyItem[]
  loading: boolean
  error: string | null
}

export const useApiKeysStore = defineStore('api-keys', {
  state: (): State => ({
    items: [],
    loading: false,
    error: null,
  }),

  actions: {
    async load() {
      this.loading = true
      this.error = null
      try {
        this.items = await api.list()
      } catch (err) {
        this.error = mapError(err)
      } finally {
        this.loading = false
      }
    },

    async revoke(id: string): Promise<boolean> {
      this.error = null
      try {
        await api.revoke(id)
        await this.load()
        return true
      } catch (err) {
        this.error = mapError(err)
        return false
      }
    },

    reset() {
      this.items = []
      this.error = null
    },
  },
})

function mapError(err: unknown): string {
  if (err instanceof ApiError && err.status === 401) {
    return 'Session expired. Sign in again.'
  }
  return 'Something went wrong. Try again.'
}
