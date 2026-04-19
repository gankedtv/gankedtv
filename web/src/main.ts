import { createApp } from 'vue'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { useThemeStore } from './stores/theme'
import { useAuthStore } from './stores/auth'
import { configureAuth } from './api/client'
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

app.use(router)
app.mount('#app')
