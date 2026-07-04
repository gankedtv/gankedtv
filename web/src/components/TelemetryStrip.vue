<script setup lang="ts">
// Inline stat strip: condensed value + small muted label per cell, cells
// separated by a middle dot — no bordered cell chrome (Arena keeps stats
// quiet). Used on ClipView, UserView, GameView. Cells marked `action` are
// tappable (like/share live on the stat itself) and emit `cell-click`.
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
  <div class="flex flex-wrap items-center gap-x-2.5 gap-y-2">
    <template v-for="(cell, i) in cells" :key="cell.key">
      <span v-if="i > 0" class="text-text-muted" aria-hidden="true">·</span>
      <component
        :is="cell.action ? 'button' : 'div'"
        :type="cell.action ? 'button' : undefined"
        :class="[
          'group flex items-baseline gap-1.5 p-0 text-left',
          cell.action && 'cursor-pointer',
        ]"
        @click="cell.action && $emit('cell-click', cell.key)"
      >
        <span
          :class="[
            'font-condensed text-lg font-bold leading-none',
            cell.ink ? 'text-accent' : 'text-text-primary',
            cell.action && 'transition-colors duration-150 group-hover:text-accent',
          ]"
        >
          {{ cell.value }}
        </span>
        <span
          class="text-[10px] font-medium uppercase leading-none tracking-widest text-text-muted"
        >
          <slot :name="`icon-${cell.key}`" />{{ cell.label }}
        </span>
      </component>
    </template>
  </div>
</template>
