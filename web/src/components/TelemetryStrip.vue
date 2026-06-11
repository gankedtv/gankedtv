<script setup lang="ts">
// Broadcast telemetry strip: a bordered row of stat cells, mono kicker label
// over an oversized condensed numeral. Used on ClipView, UserView, GameView,
// LeaderboardsView. Cells marked `action` are tappable (like/share live on
// the stat itself) and emit `cell-click`. Collapses to a 2×2 grid on phones.
export interface TelemetryCell {
  key: string
  label: string
  value: string
  ink?: boolean
  action?: boolean
}

defineProps<{
  cells: TelemetryCell[]
}>()

defineEmits<{
  'cell-click': [key: string]
}>()
</script>

<template>
  <div
    class="grid border border-border tablet:auto-cols-fr tablet:grid-flow-col max-tablet:grid-cols-2"
  >
    <component
      v-for="cell in cells"
      :key="cell.key"
      :is="cell.action ? 'button' : 'div'"
      :class="[
        'group flex flex-col gap-2 border-border p-4 text-left',
        'tablet:border-l tablet:first:border-l-0',
        'max-tablet:odd:border-r max-tablet:nth-[n+3]:border-t',
        cell.action && 'cursor-pointer transition-colors duration-150 hover:bg-surface-raised',
      ]"
      @click="cell.action && $emit('cell-click', cell.key)"
    >
      <span class="font-mono text-[9px] uppercase leading-none tracking-[0.18em] text-text-muted">
        <slot :name="`icon-${cell.key}`" />{{ cell.label }}
      </span>
      <span
        :class="[
          'font-heading text-[26px] font-bold leading-none',
          cell.ink ? 'text-ink' : 'text-text-primary',
          cell.action && 'transition-colors duration-150 group-hover:text-ink',
        ]"
      >
        {{ cell.value }}
      </span>
    </component>
  </div>
</template>
