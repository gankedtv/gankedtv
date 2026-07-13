import { defineStore } from 'pinia'
import { presence as api, type PresenceSummary } from '@/api/presence'

// Cadence: the server's online window is 120 s (PRESENCE_WINDOW_SECONDS); polling
// at 45 s keeps this browser inside the window with margin. Unlike notifications,
// presence polls for anonymous visitors too — they count as online.
const POLL_INTERVAL_MS = 45_000

interface State {
  // null = unknown (endpoint absent/erroring/not yet fetched). Per the missing-data
  // policy the UI renders nothing in that state — never a zero or a placeholder.
  online: number | null
  followsOnline: PresenceSummary['followsOnline']
  followsOnlineCount: number
  pollTimer: ReturnType<typeof setInterval> | null
}

export const usePresenceStore = defineStore('presence', {
  state: (): State => ({
    online: null,
    followsOnline: [],
    followsOnlineCount: 0,
    pollTimer: null,
  }),

  actions: {
    startPolling() {
      if (this.pollTimer !== null) return
      void this.refresh()
      this.pollTimer = setInterval(() => {
        void this.refresh()
      }, POLL_INTERVAL_MS)
    },

    stopPolling() {
      if (this.pollTimer !== null) {
        clearInterval(this.pollTimer)
        this.pollTimer = null
      }
    },

    async refresh() {
      try {
        const summary = await api.summary()
        this.online = summary.online
        this.followsOnline = summary.followsOnline
        this.followsOnlineCount = summary.followsOnlineCount
      } catch {
        // Any failure (disabled endpoint → 503, network blip) hides the indicator
        // until a later poll succeeds — silent by design.
        this.online = null
        this.followsOnline = []
        this.followsOnlineCount = 0
      }
    },
  },
})
