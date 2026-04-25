<script setup lang="ts">
import { useThemeStore, THEME_NAMES, type ThemeName } from '@/stores/theme'

const theme = useThemeStore()

const LABELS: Record<ThemeName, string> = {
  underground: 'U',
  tactical: 'T',
  arcade: 'A',
}
</script>

<template>
  <div
    class="inline-flex h-9 items-center overflow-hidden rounded-md border border-border max-tablet:hidden"
    role="group"
    aria-label="Theme"
  >
    <button
      v-for="name in THEME_NAMES"
      :key="name"
      :class="[
        'h-full w-7 cursor-pointer border-r border-border font-mono text-[11px] font-medium tracking-[0.04em] transition-colors duration-150 last:border-r-0',
        theme.name === name
          ? 'bg-brand text-white'
          : 'bg-transparent text-text-secondary hover:bg-surface-overlay hover:text-text-primary',
      ]"
      :aria-label="`Switch to ${name} theme`"
      :aria-pressed="theme.name === name"
      :title="name"
      @click="theme.setName(name)"
    >
      {{ LABELS[name] }}
    </button>
  </div>
</template>
