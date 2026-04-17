import { createApp } from 'vue'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { useThemeStore } from './stores/theme'
import './assets/main.css'

const app = createApp(App)
const pinia = createPinia()

app.use(pinia)

// Initialize theme before mounting to avoid flash of wrong theme
const themeStore = useThemeStore(pinia)
themeStore._apply()

app.use(router)
app.mount('#app')
