<script setup lang="ts">
import { useThemeStore } from '@/stores/theme'
import IconSun from './icons/IconSun.vue'
import IconMoon from './icons/IconMoon.vue'

const theme = useThemeStore()
</script>

<template>
  <button
    type="button"
    class="inline-flex h-9 w-9 cursor-pointer items-center justify-center rounded-md border border-border bg-transparent text-text-secondary transition-all duration-150 hover:border-border-hover hover:text-text-primary"
    :title="theme.isDark ? 'Switch to light' : 'Switch to dark'"
    :aria-label="theme.isDark ? 'Switch to light mode' : 'Switch to dark mode'"
    :aria-pressed="theme.isDark"
    @click="theme.toggle()"
  >
    <IconSun v-if="theme.isDark" :size="16" />
    <IconMoon v-else :size="16" />
  </button>
</template>
