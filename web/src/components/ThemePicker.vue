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
  <div class="theme-picker" role="group" aria-label="Theme">
    <button
      v-for="name in THEME_NAMES"
      :key="name"
      class="theme-picker__btn"
      :class="{ 'theme-picker__btn--active': theme.name === name }"
      :aria-label="`Switch to ${name} theme`"
      :aria-pressed="theme.name === name"
      :title="name"
      @click="theme.setName(name)"
    >
      {{ LABELS[name] }}
    </button>
  </div>
</template>

<style scoped>
.theme-picker {
  display: inline-flex;
  align-items: center;
  height: 36px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  overflow: hidden;
}

.theme-picker__btn {
  width: 28px;
  height: 100%;
  font-family: var(--font-mono);
  font-size: 11px;
  font-weight: 500;
  letter-spacing: 0.04em;
  color: var(--color-text-secondary);
  background: transparent;
  border: none;
  border-right: 1px solid var(--color-border);
  cursor: pointer;
  transition:
    color 150ms,
    background-color 150ms;
}

.theme-picker__btn:last-child {
  border-right: none;
}

.theme-picker__btn:hover {
  color: var(--color-text-primary);
  background: var(--color-surface-overlay);
}

.theme-picker__btn--active,
.theme-picker__btn--active:hover {
  background: var(--color-brand);
  color: #fff;
}

/* Mobile: hide picker to free space for nav + actions. Light/dark toggle stays visible. */
@media (max-width: 720px) {
  .theme-picker {
    display: none;
  }
}
</style>
