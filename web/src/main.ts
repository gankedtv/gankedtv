import { createApp } from 'vue'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { useThemeStore } from './stores/theme'
import { useAuthStore } from './stores/auth'
import { configureAuth } from './api/client'
import { initAnalytics } from './lib/analytics'
import { initSentry } from './lib/sentry'
import './assets/main.css'

const app = createApp(App)
const pinia = createPinia()

app.use(pinia)

// Initialize theme before mounting to avoid flash of wrong theme
const themeStore = useThemeStore(pinia)
themeStore.applyToDOM()

// Wire auth store callbacks into the api client before any requests are made
const auth = useAuthStore(pinia)
configureAuth({
  getAccessToken: () => auth.accessToken,
  getRefreshToken: () => auth.refreshToken,
  onTokenRefreshed: (token, refresh) => auth.setSession(token, refresh),
  onRefreshFailed: () => auth.logout(),
})

await auth.bootstrap()

// No-op unless VITE_GA_MEASUREMENT_ID is set at build time (production only).
initAnalytics(import.meta.env.VITE_GA_MEASUREMENT_ID)

// No-op unless VITE_SENTRY_DSN is set. Before `app.use(router)` so the router/tracing
// integration is registered against the router instance.
initSentry(app, router)

app.use(router)
app.mount('#app')
