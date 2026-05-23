import { defineStore } from 'pinia'
import { notifications as api, type NotificationItem } from '@/api/notifications'

// Cadence: every 30 s while authenticated. v1 polls; SSE / WebSocket is a Phase 4 follow-up.
const POLL_INTERVAL_MS = 30_000

const DROPDOWN_PREVIEW_LIMIT = 10
const PAGE_LIMIT = 20

interface State {
  items: NotificationItem[]
  cursor: string | null
  unreadCount: number
  // null when not currently polling. Stored on state so HMR / repeat startPolling() calls
  // don't leak multiple intervals.
  pollTimer: ReturnType<typeof setInterval> | null
  loading: boolean
  loadingMore: boolean
  errored: boolean
}

export const useNotificationsStore = defineStore('notifications', {
  state: (): State => ({
    items: [],
    cursor: null,
    unreadCount: 0,
    pollTimer: null,
    loading: false,
    loadingMore: false,
    errored: false,
  }),

  getters: {
    hasMore: (state): boolean => state.cursor !== null,
  },

  actions: {
    startPolling() {
      // Idempotent. App.vue watches `auth.isAuthenticated` and may fire start multiple times
      // (e.g. on every store hydration); the guard keeps polling to a single interval.
      if (this.pollTimer !== null) return
      // Fire once immediately so the badge is correct without waiting 30 s.
      void this.refreshUnreadCount()
      this.pollTimer = setInterval(() => {
        void this.refreshUnreadCount()
      }, POLL_INTERVAL_MS)
    },

    stopPolling() {
      if (this.pollTimer !== null) {
        clearInterval(this.pollTimer)
        this.pollTimer = null
      }
    },

    // Resets everything to logged-out defaults. Called on auth logout so a subsequent login
    // doesn't show stale notifications from the previous user.
    reset() {
      this.stopPolling()
      this.items = []
      this.cursor = null
      this.unreadCount = 0
      this.loading = false
      this.loadingMore = false
      this.errored = false
    },

    async refreshUnreadCount() {
      try {
        const { count } = await api.unreadCount()
        this.unreadCount = count
      } catch {
        // Polling failures are silent — a transient 5xx shouldn't surface a UI error every 30 s.
        // The next tick retries; if auth has truly broken the api client will trigger logout.
      }
    },

    async loadFirstPage(limit: number = DROPDOWN_PREVIEW_LIMIT) {
      this.loading = true
      this.errored = false
      try {
        const page = await api.list({ limit })
        this.items = page.items
        this.cursor = page.nextCursor
      } catch {
        this.errored = true
      } finally {
        this.loading = false
      }
    },

    async loadMore(limit: number = PAGE_LIMIT) {
      if (this.cursor === null || this.loadingMore) return
      this.loadingMore = true
      try {
        const page = await api.list({ cursor: this.cursor, limit })
        this.items.push(...page.items)
        this.cursor = page.nextCursor
      } catch {
        this.errored = true
      } finally {
        this.loadingMore = false
      }
    },

    async markAllRead() {
      // Optimistic: zero the badge and stamp rows locally before the request lands so the UI
      // doesn't flash. A server failure leaves things in a slightly stale state until the next
      // poll corrects them — acceptable for a low-stakes UI action.
      const now = new Date().toISOString()
      this.unreadCount = 0
      this.items = this.items.map((n) => (n.readAt === null ? { ...n, readAt: now } : n))
      try {
        await api.markAllRead()
      } catch {
        // Re-sync on next poll tick; nothing to do here.
      }
    },

    async markOneRead(id: string) {
      const item = this.items.find((n) => n.id === id)
      const wasUnread = item?.readAt === null
      if (item && wasUnread) {
        item.readAt = new Date().toISOString()
        this.unreadCount = Math.max(0, this.unreadCount - 1)
      }
      try {
        await api.markOneRead(id)
      } catch {
        // Same optimistic-update rationale as markAllRead.
      }
    },
  },
})
