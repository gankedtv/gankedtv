<script setup lang="ts">
import { useThemeStore } from '@/stores/theme'
import IconSun from './icons/IconSun.vue'
import IconMoon from './icons/IconMoon.vue'

const theme = useThemeStore()
</script>

<template>
  <button
    type="button"
    class="inline-flex size-8.5 cursor-pointer items-center justify-center rounded-lg border border-border bg-transparent text-text-secondary transition-colors duration-150 hover:border-border-strong hover:text-text-primary"
    :title="theme.isDark ? 'Switch to light' : 'Switch to dark'"
    :aria-label="theme.isDark ? 'Switch to light mode' : 'Switch to dark mode'"
    :aria-pressed="theme.isDark"
    @click="theme.toggle()"
  >
    <IconSun v-if="theme.isDark" :size="16" />
    <IconMoon v-else :size="16" />
  </button>
</template>
