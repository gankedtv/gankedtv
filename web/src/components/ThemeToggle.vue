<script setup lang="ts">
import { useThemeStore } from '@/stores/theme'

const theme = useThemeStore()
</script>

<template>
  <button
    class="flex h-9 w-9 shrink-0 cursor-pointer items-center justify-center rounded-full border border-border bg-transparent text-text-secondary transition-[background-color,border-color,color] duration-200 hover:border-border-hover hover:bg-surface-overlay hover:text-text-primary"
    :aria-label="theme.isDark ? 'Switch to light mode' : 'Switch to dark mode'"
    @click="theme.toggle()"
  >
    <Transition
      mode="out-in"
      enter-active-class="transition-[opacity,transform] duration-150 ease-[ease]"
      leave-active-class="transition-[opacity,transform] duration-150 ease-[ease]"
      enter-from-class="opacity-0 -rotate-90 scale-50"
      leave-to-class="opacity-0 rotate-90 scale-50"
    >
      <svg
        v-if="theme.isDark"
        key="sun"
        xmlns="http://www.w3.org/2000/svg"
        width="18"
        height="18"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <circle cx="12" cy="12" r="4" />
        <path
          d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"
        />
      </svg>
      <svg
        v-else
        key="moon"
        xmlns="http://www.w3.org/2000/svg"
        width="18"
        height="18"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
      </svg>
    </Transition>
  </button>
</template>
