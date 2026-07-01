<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import IconMoreVertical from '@/components/icons/IconMoreVertical.vue'
import IconMoreHorizontal from '@/components/icons/IconMoreHorizontal.vue'

export interface KebabMenuItem {
  label: string
  onClick: () => void
  // `danger` paints the label in the accent — used for destructive actions (Delete, Sign out, Ban).
  variant?: 'default' | 'danger'
  // Hide the item without forcing the caller to filter its own list. Convenient when
  // the visibility depends on reactive state (e.g. owner-only actions).
  hidden?: boolean
}

withDefaults(
  defineProps<{
    items: KebabMenuItem[]
    ariaLabel?: string
    // Two orientations cover the existing kebabs in ClipView (vertical) and UserView
    // (horizontal). Defaults to vertical to match the convention everywhere else.
    iconOrientation?: 'vertical' | 'horizontal'
    // `outlined` = bordered surface-raised button (UserView, profile header).
    // `plain` = transparent, hover-only background (ClipView, in a button cluster).
    triggerVariant?: 'outlined' | 'plain'
  }>(),
  {
    ariaLabel: 'More options',
    iconOrientation: 'vertical',
    triggerVariant: 'outlined',
  },
)

const open = ref(false)
const rootRef = ref<HTMLDivElement | null>(null)
const menuRef = ref<HTMLDivElement | null>(null)
// Nudge the right-0 dropdown back on-screen when the trigger sits near a viewport edge.
const shiftX = ref(0)

function toggle() {
  open.value = !open.value
}
function close() {
  open.value = false
}

function clampToViewport() {
  const el = menuRef.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  const margin = 8
  if (rect.left < margin) shiftX.value = margin - rect.left
  else if (rect.right > window.innerWidth - margin)
    shiftX.value = window.innerWidth - margin - rect.right
  else shiftX.value = 0
}

function onItemClick(item: KebabMenuItem) {
  close()
  item.onClick()
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') close()
}

function onDocumentClick(e: MouseEvent) {
  // Outside-click closer. Root contains both the trigger AND the dropdown, so a click
  // inside the dropdown doesn't trip this — the item's @click handler already closes.
  if (rootRef.value && !rootRef.value.contains(e.target as Node)) close()
}

// Attach listeners only while the menu is open so background renders don't pay for them.
// The capture-phase click listener fires before any inner @click.stop can swallow it.
watch(open, (isOpen) => {
  if (isOpen) {
    window.addEventListener('keydown', onKeydown)
    window.addEventListener('click', onDocumentClick, true)
    nextTick(clampToViewport)
  } else {
    shiftX.value = 0
    window.removeEventListener('keydown', onKeydown)
    window.removeEventListener('click', onDocumentClick, true)
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  window.removeEventListener('click', onDocumentClick, true)
})
</script>

<template>
  <div ref="rootRef" class="relative">
    <button
      type="button"
      :class="[
        'flex cursor-pointer items-center justify-center rounded-lg transition-colors duration-150',
        triggerVariant === 'outlined'
          ? 'h-9 w-9 border border-border bg-transparent text-text-secondary hover:border-accent hover:text-accent'
          : 'h-7 w-7 text-text-secondary hover:bg-surface-high',
      ]"
      :aria-label="ariaLabel"
      aria-haspopup="true"
      :aria-expanded="open"
      @click.stop="toggle"
    >
      <component
        :is="iconOrientation === 'horizontal' ? IconMoreHorizontal : IconMoreVertical"
        :size="triggerVariant === 'outlined' ? 14 : 16"
      />
    </button>

    <div
      v-if="open"
      ref="menuRef"
      role="menu"
      :style="shiftX ? { transform: `translateX(${shiftX}px)` } : undefined"
      class="absolute right-0 top-full z-20 mt-1 min-w-36 overflow-hidden rounded-lg border border-border-strong bg-surface-base"
    >
      <button
        v-for="item in items.filter((i) => !i.hidden)"
        :key="item.label"
        type="button"
        role="menuitem"
        :class="[
          'w-full cursor-pointer px-3 py-2 text-left text-xs font-medium transition-colors duration-150 hover:bg-surface-high',
          item.variant === 'danger' ? 'text-accent' : 'text-text-secondary hover:text-text-primary',
        ]"
        @click="onItemClick(item)"
      >
        {{ item.label }}
      </button>
    </div>
  </div>
</template>
