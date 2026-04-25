<script setup lang="ts">
import { useThemeStore } from '@/stores/theme'
import IconSun from './icons/IconSun.vue'
import IconMoon from './icons/IconMoon.vue'

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
      <IconSun v-if="theme.isDark" key="sun" :size="18" />
      <IconMoon v-else key="moon" :size="18" />
    </Transition>
  </button>
</template>
